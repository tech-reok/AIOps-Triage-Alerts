# 🚨 Detected fail

- What failed: The C# API process in the container fails immediately during startup due to an unhandled exception.
- Affected container/service: csharp-api-prod (CsharpAppBuildDocker.Api)
- Relevant error (repeated in logs):
  ```
  Unhandled exception. System.Exception: Startup failure triggered by FAIL_ON_STARTUP environment variable.
     at Program.<Main>$(String[] args) in /src/src/CsharpAppBuildDocker.Api/Program.cs:line 32
  ```
- Exit code / signal: Not present in provided logs.
- Timestamp/sequence: No timestamps supplied. The identical stack trace repeats many times (implies repeated restarts).
- Impact: Service does not start; API unavailable. Likely continuous crash-restart of the container/pod.
- Evidence:
  - The exception message explicitly names FAIL_ON_STARTUP as the trigger.
  - Stack trace consistently points to Program.Main at Program.cs:line 32 for every crash.

# 🔍 Root cause analisys (RCA)

- Root cause (most likely, based strictly on supplied logs)
  - Confirmed fact: The application throws an unhandled System.Exception from Program.Main (Program.cs:line 32) with message "Startup failure triggered by FAIL_ON_STARTUP environment variable." This is the immediate cause of process termination.
  - Most likely technical root cause: Startup code contains logic that intentionally throws when FAIL_ON_STARTUP is set (or treated as enabled) and that exception is not caught, causing the process to exit.

- Evidence
  - Direct log text: "Startup failure triggered by FAIL_ON_STARTUP environment variable."
  - Matching stack frame: Program.<Main> … Program.cs:line 32, repeated.

- Failure chain
  1. Container starts and executes application entrypoint.
  2. In Program.Main (line 32) startup logic checks environment/config and hits the condition relating to FAIL_ON_STARTUP.
  3. Code throws System.Exception("Startup failure triggered by FAIL_ON_STARTUP environment variable").
  4. Exception is unhandled → process terminates.
  5. Orchestrator restarts container (observed repeated identical traces) → loop repeats.

- Contributing factors
  - Application design: throwing an unhandled exception on startup for a configuration flag.
  - Deployment/configuration: FAIL_ON_STARTUP is present or evaluated as enabled in the runtime environment (source of variable not shown).
  - Lack of defensive handling: no graceful failure path, no clear logging of the variable value, and possible missing readiness/liveness probe tuning.

- Confidence
  - Immediate cause (exception thrown at Program.cs:32 due to FAIL_ON_STARTUP): High.
  - Why the env var is set (intentional test vs misconfiguration): Medium — logs do not show env values or deployment manifests.

- Uncertainties / required additional evidence
  - Exact value and presence of FAIL_ON_STARTUP inside the running container (not shown).
  - Deployment manifests / CI/CD steps that set environment variables.
  - The exact code at Program.cs:line 32.
  - Pod status events (CrashLoopBackOff, restart counts, timestamps).

If you need higher confidence, collect: `kubectl describe pod`, `kubectl logs --previous`, `kubectl exec <pod> -- printenv`, and the Program.cs snippet around line 32.

# 💡 Recommended solution

1) Immediate mitigation (restore service quickly)
- Remove or disable the env var in production deployment, then restart pods.
  - Kubernetes: remove env var and restart rollout
    ```bash
    # remove FAIL_ON_STARTUP from deployment env
    kubectl set env deployment/<deployment-name> FAIL_ON_STARTUP- -n <namespace>
    kubectl rollout restart deployment/<deployment-name> -n <namespace>
    ```
  - If you cannot change deployment immediately, scale down to stop noisy restarts:
    ```bash
    kubectl scale deployment/<deployment-name> --replicas=0 -n <namespace>
    ```
- Docker: run new container without the env var
  ```bash
  docker run --rm -e FAIL_ON_STARTUP=false <image>   # or omit -e entirely
  ```

2) Permanent fix (application & deployment changes)
- Modify application startup so the flag does not cause an unhandled throw:
  - Require explicit value (e.g., only fail when value == "true") and handle it gracefully:
    ```csharp
    // Program.cs (safer pattern)
    var env = Environment.GetEnvironmentVariable("FAIL_ON_STARTUP");
    if (!string.IsNullOrEmpty(env) && env.Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("FAIL_ON_STARTUP=true — aborting startup by design.");
        // perform cleanup/logging then exit with non-zero code (avoid unhandled throw)
        Environment.Exit(1);
    }
    ```
  - Add top-level try/catch in Main to log and exit cleanly (so you get structured logs and predictable exit behavior).
- Make FAIL_ON_STARTUP a clearly named, test-only flag (e.g., INTENTIONAL_STARTUP_FAILURE) and ensure it is not injected into production manifests.
- Update deployment templates/CI to prevent test-only env vars from reaching prod.

3) Preventive actions (monitoring, process control, infra)
- Add alerts:
  - Alert on pod restarts > N within M minutes.
  - Alert on CrashLoopBackOff.
- Improve health checks:
  - Add readiness probe that waits until startup completes.
  - Tune liveness probe to avoid immediate kills during short startup/diagnostics.
- CI gating:
  - Add a deployment-time check to disallow critical test env vars in prod manifests.
- Logging:
  - Emit structured logs with env var context (but avoid logging secrets).

4) Verification (commands / checks)
- Confirm env var is removed and pods are running:
  ```bash
  kubectl get pods -n <namespace>
  kubectl describe pod <pod-name> -n <namespace>    # check Events and restartCount
  kubectl exec -it <pod-name> -n <namespace> -- printenv | grep FAIL_ON_STARTUP || echo "not set"
  kubectl logs -f <pod-name> -n <namespace> --tail=200
  ```
- Confirm service responds:
  ```bash
  curl -fsS http://<service-host>:<port>/health || echo "health check failed"
  ```
- If changing code, run container locally with and without the variable:
  ```bash
  # should fail when explicitly true (if intended), otherwise start normally
  docker build -t test-img .
  docker run --rm -e FAIL_ON_STARTUP=true test-img   # expect controlled exit/log
  docker run --rm test-img                            # expect running service
  ```

Notes: do not reintroduce this test-only variable into production manifests. If the flag is needed for chaos/test runs, gate it behind environment-specific configs and add ownership documentation.