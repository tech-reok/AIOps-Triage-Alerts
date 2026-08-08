# 🚨 Detected fail

- What failed (concise): Docker daemon reports the container named `admiring_morse` cannot be found when queried — operator cannot inspect or retrieve logs for that container.
- Affected container/service: container id/name: `admiring_morse` (service: CsharpAppBuildDocker.Api in prior logs).
- Relevant error message:
  ```
  Error response from daemon: No such container: admiring_morse
  ```
- Exit code / signal: Not provided by the daemon message.
- Timestamps / sequence: Not provided. Earlier supplied logs (separately) show repeated startup exceptions from Program.cs:line 32 referencing `FAIL_ON_STARTUP` — those indicate the application previously crashed repeatedly.
- Impact:
  - Cannot access container logs/inspect the container by name.
  - Operational impact: inability to diagnose container state with docker CLI; service likely down or restarted/removed.
- Evidence:
  - Direct daemon response above.
  - (Context) earlier logs (repeated) contained:  
    `Unhandled exception. System.Exception: Startup failure triggered by FAIL_ON_STARTUP environment variable. at Program.<Main>… Program.cs:line 32` — indicates application-level crash prior to this daemon error.

# 🔍 Root cause analisys (RCA)

- Root cause (most likely, based on supplied evidence)
  - Confirmed fact: Docker returned “No such container: admiring_morse” when asked about that name.
  - Most likely explanation (based on earlier crash logs + this daemon error): the process inside the container threw an unhandled exception at startup (FAIL_ON_STARTUP) and the container either exited and was removed (e.g., started with --rm) or the orchestrator removed/recreated it — so the container name no longer exists on the node.
- Evidence supporting conclusion
  - Explicit daemon error saying the container name does not exist.
  - Earlier repeated application exceptions originating at Program.cs:line 32 referencing FAIL_ON_STARTUP (strong signal the app was crashing on startup).
- Failure chain (plausible sequence)
  1. Container started and application executed Program.Main.
  2. Startup logic threw an unhandled exception (logs showed FAIL_ON_STARTUP triggered).
  3. Container process exited.
  4. Either container was auto-removed (docker run --rm) or orchestrator/cleanup removed the container record.
  5. Subsequent `docker` query by name returns “No such container”.
- Contributing factors
  - Application intentionally throwing at startup when `FAIL_ON_STARTUP` is set.
  - Container run flags or orchestrator configuration that remove or immediately replace failed containers (--rm, short-lived restart policies, or automatic cleanup).
  - Lack of persistent log capture or retained container artifacts for post-mortem.
- Confidence: Medium.
  - High confidence that the container is not present on the node (daemon message).
  - Moderate confidence that prior startup exception caused exit and removal (we have prior logs showing the exception but no container-level events).
- Uncertainties (not determinable from provided logs)
  - Whether the container was removed automatically by `--rm`, manually, or by orchestration (k8s/docker-compose).
  - Exact container lifecycle events and timestamps (no `docker ps -a`, `docker events`, or orchestrator events provided).
  - Current state on orchestrator (pod status) or host (other containers with same image).

# 💡 Recommended solution

Immediate mitigation (restore visibility / diagnose)
1. Check current containers and recent terminated containers on the host:
   ```bash
   # list running containers
   docker ps

   # list all containers including exited
   docker ps -a | grep admiring_morse -C 3
   ```
2. If using Kubernetes, inspect pods/events instead:
   ```bash
   kubectl get pods -A | grep -i <app-or-label>
   kubectl describe pod <pod-name> -n <namespace>
   kubectl logs <pod-name> -n <namespace> --previous
   ```
3. Inspect daemon events to see create/exit/remove timeline:
   ```bash
   # stream or show recent events
   docker events --since "$(date --iso-8601=seconds -d '5 minutes ago')" --until "$(date --iso-8601=seconds)"
   ```
4. If no container exists and you need logs, check centralized logging (ELK/Fluentd) or orchestrator logs; otherwise re-run container without auto-remove to capture diagnostics:
   ```bash
   docker run --name admiring_morse_debug -e FAIL_ON_STARTUP=false -it <image> /bin/sh
   # or without --rm so it remains after exit
   ```

Permanent fix (application + deployment)
1. Remove/disable the test flag in production:
   - Ensure `FAIL_ON_STARTUP` is not injected into production environment manifests.
2. Change application startup behavior:
   - Avoid unhandled throws on config flags. Use controlled exit/logging or default safe behavior.
   - Add a top-level try/catch in Program.Main to emit structured diagnostics and exit cleanly.
3. Deployment changes:
   - Do not start critical containers with `--rm` in production.
   - If using Docker Compose / k8s, configure restartPolicy and retain logs/artifacts to aid debugging.

Preventive actions (observability & policy)
- Capture container stdout/stderr into centralized logs; enable log persistence even for short-lived containers.
- Alert on repeated startup exceptions and on unusually high container removal rates.
- Add probe-based readiness/liveness rules to avoid rapid crash-restart churn.
- CI/CD: prevent test-only environment variables from being applied to production manifests.

Verification (commands to confirm resolution)
- Confirm container or pod is present and running:
  ```bash
  docker ps --filter "name=admiring_morse"
  # or Kubernetes:
  kubectl get pods -l app=<label> -n <namespace>
  ```
- Confirm `FAIL_ON_STARTUP` not set in environment for running container:
  ```bash
  docker run --rm --entrypoint="" <image> env | grep FAIL_ON_STARTUP || echo "not set"
  # or inspect a running container:
  kubectl exec -it <pod-name> -n <namespace> -- printenv | grep FAIL_ON_STARTUP || echo "not set"
  ```
- Reproduce startup locally with debug flags and retained container to capture logs:
  ```bash
  docker run --name admiring_morse_debug -e FAIL_ON_STARTUP=false <image>
  docker logs -f admiring_morse_debug
  ```

If you want, provide:
- Output of `docker ps -a`, `docker events` around the incident time, or `kubectl describe pod` / `kubectl logs --previous` so I can pinpoint whether the container was removed, restarted, or never created.