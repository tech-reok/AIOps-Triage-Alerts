# 🚨 Detected fail

- What failed (concise): Application process in container crashed repeatedly due to an unhandled exception thrown during startup.
- Affected container/service: `csharp-api-prod` (service: C# API)
- Relevant error message(s) (log excerpt):
  ```
  Unhandled exception. System.Exception: Startup failure triggered by FAIL_ON_STARTUP environment variable.
     at Program.<Main>$(String[] args) in /src/src/CsharpAppBuildDocker.Api/Program.cs:line 32
  ```
  (This same message repeats multiple times in the supplied logs.)
- Exit code / signal: Not present in provided logs. Process termination implied by unhandled exception; no explicit exit code recorded here.
- Timestamps / sequence: No timestamps supplied. Logs show the same stack trace repeated indicating repeated process starts/crashes (likely a restart loop).
- Impact:
  - The API is not starting; service is unavailable.
  - Likely container restart loop (service instability and failed health checks).
- Evidence:
  - The exception message explicitly names the trigger: "Startup failure triggered by FAIL_ON_STARTUP environment variable."
  - Stack trace consistently points to Program.Main at Program.cs:line 32, indicating the throw originates from startup logic.

# 🔍 Root cause analisys (RCA)

- Root cause (most likely, based strictly on supplied logs):
  - The application explicitly throws an unhandled System.Exception during startup because of logic that reacts to the `FAIL_ON_STARTUP` environment variable (exception message and stack trace show the throw originates at Program.Main, Program.cs:line 32).
- Evidence:
  - Direct log message: "Startup failure triggered by FAIL_ON_STARTUP environment variable."
  - Consistent stack frame: Program.<Main> … Program.cs:line 32 (same source line for every crash).
- Failure chain (step-by-step):
  1. Container starts and runs the application entrypoint.
  2. During Program.Main startup flow (Program.cs line 32), code evaluates `FAIL_ON_STARTUP` (or otherwise decides to fail based on that env var).
  3. Code throws System.Exception: "Startup failure triggered by FAIL_ON_STARTUP environment variable."
  4. Exception is unhandled -> process exits/crashes.
  5. Orchestrator / container runtime restarts container -> loop repeats (repeated identical logs).
- Contributing factors:
  - Application design: startup code throws an unhandled exception (no try/catch or graceful handling).
  - Deployment/configuration: environment variable `FAIL_ON_STARTUP` was set (or code treats its absence as a failure) causing intentional crash.
  - Lack of defensive startup checks or configuration validation that would allow safe defaults.
  - Missing or misconfigured health/readiness checks could allow the pod to be repeatedly restarted rather than failover.
- Confidence: High that the immediate cause is the application throwing due to `FAIL_ON_STARTUP` (logs are explicit). Medium regarding why the env var was set (intentional test, CI/CD mistake, chaos testing, or misconfiguration) because environment and deployment details were not provided.
- Uncertainties / unknowns (not determinable from supplied logs):
  - Whether `FAIL_ON_STARTUP` is set to a specific value (true/1) or whether the application treats presence/non-presence differently.
  - Whether the environment variable was set intentionally (test/feature flag) or accidentally by deployment pipeline.
  - Exact container runtime exit code or orchestrator pod status (CrashLoopBackOff) because pod-level events and timestamps were not provided.
  - Any other errors or earlier logs leading up to Program.Main (only the crash stacktrace provided).

If root cause needs higher confidence, collect:
- Current pod/container environment variables (from orchestrator or docker inspect).
- kubectl describe pod and event stream (CrashLoopBackOff, restart policy events).
- Full container stdout/stderr with timestamps and restart count.
- Program.cs source around line 32 to confirm exact code path and condition.

# 💡 Recommended solution

Prioritized remediation steps and verifications.

1) Immediate mitigation (restore service quickly)
- Option A — Disable the failing behavior by removing/unsetting the env var in the running deployment (recommended if variable was set accidentally):
  - Kubernetes (unset env var from Deployment and trigger rollout):
    ```bash
    # remove env var from deployment
    kubectl set env deployment/<deployment-name> FAIL_ON_STARTUP- -n <namespace>
    # or set it to false
    kubectl set env deployment/<deployment-name> FAIL_ON_STARTUP=false -n <namespace>

    # then restart pods to pick up change
    kubectl rollout restart deployment/<deployment-name> -n <namespace>
    ```
  - Docker (restart container without the env var):
    ```bash
    # inspect current container env
    docker inspect --format='{{range .Config.Env}}{{println .}}{{end}}' <container-id> | grep FAIL_ON_STARTUP

    # run new container without the env var (example)
    docker run -e FAIL_ON_STARTUP= false ... <image>
    ```
- Option B — If you cannot change deployment immediately, scale replicas to 0 to stop serving bad instances while you fix configuration:
  ```bash
  kubectl scale deployment/<deployment-name> --replicas=0 -n <namespace>
  ```
- Verification (immediate):
  ```bash
  # after change: check pod status
  kubectl get pods -n <namespace> -l app=<label> -o wide

  # follow logs (should stop showing the exception)
  kubectl logs -f <pod-name> -n <namespace>
  ```

2) Permanent fix (code and deployment changes)
- Change application startup behavior to avoid throwing an unhandled exception for an environment flag:
  - Make FAIL_ON_STARTUP opt-in and explicit (only fail if var equals a specific value like "true").
  - Do not throw an unhandled exception on startup for configuration issues; instead:
    - Log an error.
    - Exit gracefully with clear exit code if you must abort, or
    - Default to a safe behavior (e.g., continue with warnings).
  - Example (C#) safer pattern:
    ```csharp
    // Program.cs (pseudo)
    var failOnStartup = Environment.GetEnvironmentVariable("FAIL_ON_STARTUP");
    if (!string.IsNullOrEmpty(failOnStartup) && failOnStartup.Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("FAIL_ON_STARTUP=true — aborting startup by design.");
        // Option A: exit gracefully with non-zero code after cleanup
        Environment.Exit(1);
        // Option B: don't throw unhandled; allow orchestrator to handle non-zero exit
    }
    ```
  - Add explicit validation and descriptive error messages; catch exceptions at top-level to perform any necessary cleanup and to emit structured logs.
- Add config gating:
  - Only enable "fail on startup" behavior in non-production environments (if this variable is used as a test/chaos switch).
  - Use more explicit config names (e.g., `INTENTIONAL_STARTUP_FAILURE=true`) and document it.
- Improve health checks:
  - Add readiness probe that returns NotReady while startup checks run.
  - Consider liveness probe tuning so transient startup failures don't immediately kill the container while investigation occurs.
- CI/CD / deployment hygiene:
  - Remove accidental propagation of test variables to production images.
  - Ensure that environment variables injected by CI/CD are environment-targeted (dev/test vs prod).

3) Preventive actions (operational, monitoring, testing)
- Monitoring & alerting:
  - Alert on repeated identical exception messages and on increasing restart counts (pod restart_count > 0).
  - Alert on CrashLoopBackOff or when pod restarts exceed a threshold.
- Logging:
  - Centralize logs so crash patterns are searchable (include structured fields: instance, pod, deployment, env).
- Tests:
  - Add integration tests or smoke tests in CI that start the container with production env and ensure it does not fail due to test-only environment variables.
- Deployment policy:
  - Enforce separation between dev/test and prod env var injection; require deployment-time review for any env var change.
- Runbooks / docs:
  - Document the purpose of any "fail on startup" env var and control access to toggling it.

4) Verification (commands and checks to confirm resolution)
- Confirm env var removed or set correctly in deployment:
  ```bash
  kubectl get deployment <deployment-name> -n <namespace> -o yaml | yq '.spec.template.spec.containers[0].env'
  # or inspect a running pod:
  kubectl exec -it <pod-name> -n <namespace> -- printenv | grep FAIL_ON_STARTUP || echo "not set"
  ```
- Confirm pod is Running and not restarting:
  ```bash
  kubectl get pods -n <namespace> -o wide
  kubectl describe pod <pod-name> -n <namespace>  # check Events and restart counts
  kubectl logs <pod-name> -n <namespace> --tail=200
  ```
- Confirm application health endpoints respond:
  ```bash
  # If service exposes /health
  curl -fsS http://<pod-ip-or-service>:<port>/health || echo "health check failed"
  ```
- Confirm no repeated exception in logs:
  ```bash
  kubectl logs -f <pod-name> -n <namespace> | grep --line-buffered "FAIL_ON_STARTUP"
  ```
- Confirm orchestrator status:
  ```bash
  kubectl get pods -n <namespace> --field-selector=status.phase!=Running
  kubectl get events -n <namespace> --sort-by='.lastTimestamp'
  ```

Notes / guidance
- If `FAIL_ON_STARTUP` is intentionally used for chaos/testing, change it to a clearly named opt-in flag and ensure it is never set in production manifests.
- If you need help locating which layer set the env var, collect:
  - Deployment manifest (k8s YAML / docker-compose / systemd unit).
  - Image entrypoint and container env as inspected at runtime (docker inspect / kubectl exec printenv).
  - CI/CD pipeline logs that perform kubectl apply / docker run with env injection.

Summary
- Immediate cause: explicit throw at Program.cs:line 32 triggered by behaviour tied to `FAIL_ON_STARTUP` (logs are explicit).
- Immediate remediation: unset or set `FAIL_ON_STARTUP` to a non-failing value in the deployment and restart pods.
- Long-term: change application to not throw unhandled exceptions for environment toggles, add validation, improve probes, and prevent test-only env vars from reaching production.