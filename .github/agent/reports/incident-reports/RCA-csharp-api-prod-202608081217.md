# 🚨 Detected fail

- What failed (concise): The C# API experienced startup failures due to an unhandled exception triggered by an environment variable; subsequent container queries reported the container name missing; later logs show the app running on HTTP:8080 with an HTTPS-redirection warning.
- Affected container/service: csharp-api-prod (CsharpAppBuildDocker.Api). A Docker container queried by name `admiring_morse` also reported "No such container".
- Relevant log evidence (verbatim excerpts):
  - Repeated crash traces:
    ```
    Unhandled exception. System.Exception: Startup failure triggered by FAIL_ON_STARTUP environment variable.
       at Program.<Main>$(String[] args) in /src/src/CsharpAppBuildDocker.Api/Program.cs:line 32
    ```
    (repeated many times)
  - Docker daemon:
    ```
    Error response from daemon: No such container: admiring_morse
    ```
  - Successful bind / running instance:
    ```
    Overriding HTTP_PORTS '8080' and HTTPS_PORTS ''. Binding to values defined by URLS instead 'http://+:8080'.
    Now listening on: http://[::]:8080
    Application started. Press Ctrl+C to shut down.
    Hosting environment: Production
    Failed to determine the https port for redirect.
    ```
- Exit code / signal: Not provided in logs.
- Timestamps / sequence: No explicit timestamps were provided; order implied by log submissions (crashes → missing container query → HTTP bind logs).
- Impact:
  - Confirmed: service failed to start repeatedly (crash loop) at least once; inability to inspect a named container (`admiring_morse`) when queried; at another observed run the app started and served HTTP on port 8080 but cannot determine HTTPS port for redirect.
  - User-visible impact: API unavailable during crash loop; possible misconfiguration of TLS/redirect behavior when running.

# 🔍 Root cause analisys (RCA)

- Root cause (most likely, limited to evidence supplied)
  - Immediate confirmed cause: The process throws an unhandled System.Exception in Program.Main at Program.cs:line 32 with message explicitly naming FAIL_ON_STARTUP. This unhandled exception caused the process to terminate (confirmed by repeated identical stack traces).
  - Most likely technical root cause: A startup guard in application code intentionally throws when the environment variable FAIL_ON_STARTUP is present/enabled, and that exception is not handled, causing termination. (Evidence: exception message and stack frame.)
- Evidence
  - Direct exception messages referencing FAIL_ON_STARTUP and Program.cs:32 (repeated).
  - Docker error showing queried container name unavailable (supports that previously-running container exited/was removed): "No such container: admiring_morse".
  - Later logs show a run where the app binds to HTTP:8080 and starts, with a middleware warning: "Failed to determine the https port for redirect."
- Failure chain (step-by-step, derived from logs)
  1. Container started and application entry executed Program.Main.
  2. Code path reading FAIL_ON_STARTUP triggered a throw: System.Exception("Startup failure triggered by FAIL_ON_STARTUP environment variable").
  3. Exception was unhandled → process exited (repeat occurrences imply restart attempts).
  4. `docker` query returned "No such container" — either the failed container was removed (e.g., run with --rm) or orchestrator replaced it.
  5. In a separate run (or after env changes), the app bound to HTTP:8080 and started; HttpsRedirectionMiddleware warned because no HTTPS port was configured.
- Contributing factors
  - Application design: throwing an unhandled exception for a configuration flag.
  - Deployment/config: FAIL_ON_STARTUP present in environment (source unknown).
  - Runtime options: containers possibly run with --rm or orchestrator removes failed instances; lack of persisted logs for failed runs.
  - TLS configuration mismatch: HTTPS_PORTS empty / ASPNETCORE_URLS set to HTTP only while HttpsRedirectionMiddleware is enabled.
- Confidence
  - Immediate cause (unhandled exception at Program.cs:32 due to FAIL_ON_STARTUP): High.
  - Sequence linking container removal to crash: Medium (plausible but no container lifecycle events were provided).
  - Whether HTTPS is expected to be terminated upstream vs. in-app: Low (logs show only middleware warning).
- Uncertainties / missing data
  - Exact environment variable values inside the runtime at failure time (printenv missing).
  - Which manifest or CI step injected FAIL_ON_STARTUP.
  - Container lifecycle events and timestamps (docker events / kubectl events not provided).
  - Exact code at Program.cs:line 32.
  - Whether admiring_morse is the same runtime instance as csharp-api-prod.

# 💡 Recommended solution

Prioritized remediation actions (Immediate → Permanent), with verification commands.

1) Immediate mitigation
- Stop crash loop and restore a stable instance:
  - If using Kubernetes, scale deployment to 0 to stop restart churn, then edit and redeploy:
    ```bash
    kubectl scale deployment/<deployment> --replicas=0 -n <ns>
    ```
  - If using Docker on host, run without --rm and capture logs; or start a debug container:
    ```bash
    docker run --name csharp-api-debug -e FAIL_ON_STARTUP=false -p 8080:8080 <image>
    docker logs -f csharp-api-debug
    ```
- Remove the test flag from production manifests immediately:
  - Kubernetes:
    ```bash
    kubectl set env deployment/<deployment> FAIL_ON_STARTUP- -n <ns>
    kubectl rollout restart deployment/<deployment> -n <ns>
    ```
- Capture forensic data before further restarts:
  - `docker ps -a`, `docker logs <container-id>` or in k8s `kubectl logs <pod> --previous`, `kubectl describe pod`.

2) Permanent fixes (code + deployment)
- Application:
  - Do NOT throw unhandled exceptions for configuration-test flags. Replace with controlled behavior:
    - Validate env var explicitly (only treat "true" as enabled).
    - Use top-level try/catch in Main to log structured diagnostics and exit gracefully:
      ```csharp
      try { BuildAndRunHost(); }
      catch (Exception ex) {
          Console.Error.WriteLine(ex.ToString());
          Environment.Exit(1);
      }
      ```
- Deployment:
  - Ensure test-only env vars (FAIL_ON_STARTUP) are excluded from production via templating/CI checks.
  - Avoid running prod containers with `--rm`; configure orchestrator to keep terminated pod/container logs.
- TLS / middleware:
  - If TLS is terminated upstream (ingress/load balancer): disable HttpsRedirectionMiddleware or configure UseForwardedHeaders so the app recognizes X-Forwarded-Proto.
  - If app should host TLS: configure HTTPS binding and set ASPNETCORE_HTTPS_PORT or HttpsRedirectionOptions.HttpsPort.

3) Preventive actions (observability & CI)
- Centralized logging for stdout/stderr (persist logs of short-lived containers).
- Alerting:
  - Alert on repeated restarts (restartCount > threshold) and CrashLoopBackOff.
  - Alert on the specific exception string if present in logs.
- CI gate: reject deploys that include test-only env vars in production target.
- Add health/readiness probes and backoff tuning to avoid aggressive restart churn.

4) Verification steps / commands
- Confirm FAIL_ON_STARTUP is not set in running pods:
  ```bash
  kubectl exec -it <pod> -n <ns> -- printenv | grep FAIL_ON_STARTUP || echo "not set"
  ```
- Check pod status and restart counts:
  ```bash
  kubectl get pods -n <ns> -o custom-columns=NAME:.metadata.name,RESTARTS:.status.containerStatuses[0].restartCount,STATUS:.status.phase
  ```
- Validate no unhandled exception in logs:
  ```bash
  kubectl logs <pod> -n <ns> --tail=200 | grep "Startup failure triggered by FAIL_ON_STARTUP" || echo "no FAIL_ON_STARTUP crash found"
  ```
- Verify HTTPS redirection behavior (if enabled):
  ```bash
  curl -v http://<host>:8080/ -I
  # Expect 301/302 to https://... if redirection is configured
  ```

If you want a follow-up RCA, provide:
- `kubectl describe pod <pod>` and `kubectl logs <pod> --previous`,
- `kubectl get deployment <deployment> -o yaml` (to inspect env vars),
- the Program.cs snippet around line 32,
- and `docker ps -a` / `docker events` if using Docker directly.