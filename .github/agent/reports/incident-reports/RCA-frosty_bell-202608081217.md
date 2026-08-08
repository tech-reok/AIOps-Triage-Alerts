# 🚨 Detected fail

- What failed: Docker daemon cannot find the container named `frosty_bell` when queried.
- Affected container/service: container name `frosty_bell` (application: CsharpAppBuildDocker.Api as previously reported).
- Relevant error message:
  ```
  Error response from daemon: No such container: frosty_bell
  ```
- Exit code / signal: not available in provided logs.
- Timestamps / sequence: none provided with this message. Previous submissions included repeated startup exceptions referencing Program.cs:line 32 and a separate run showing the app bound to http://:8080; those are additional context (see Evidence).
- Impact:
  - Operator cannot inspect logs, exec into, or otherwise debug `frosty_bell` on this Docker host.
  - Potential service outage or loss of visibility; inability to perform live triage using that container name.
- Evidence:
  - Direct daemon response above (fact).
  - Earlier supplied logs (user-provided): repeated unhandled exceptions referencing `FAIL_ON_STARTUP` at Program.cs:line 32; separate run showed the app listening on http://[::]:8080 with an HTTPS redirection warning (both factually provided by you).

# 🔍 Root cause analisys (RCA)

- Root cause (most likely)
  - Confirmed fact: Docker reports the named container does not exist on the daemon queried.
  - Most likely technical root cause (hypothesis, labeled): the container previously exited and was removed (manually, via `--rm`, or by orchestration/cleanup), or the query was run against the wrong Docker host or node. Given prior logs showing unhandled startup exceptions referencing FAIL_ON_STARTUP, a likely sequence is: container started → app threw unhandled exception → container exited → container record removed → subsequent docker query fails.

- Evidence
  - "No such container: frosty_bell" (direct daemon message).
  - Prior logs you provided (earlier): repeated stack traces:
    ```
    Unhandled exception. System.Exception: Startup failure triggered by FAIL_ON_STARTUP environment variable.
       at Program.<Main>$(String[] args) in /src/.../Program.cs:line 32
    ```
    — demonstrates the application can fail at startup and terminate.
  - Other prior evidence: app successfully bound to http://:8080 in a different run (so the image can start successfully when not failing on startup).

- Failure chain (plausible)
  1. Container created and started (image runs).
  2. On startup, code in Program.Main evaluated environment/config; if `FAIL_ON_STARTUP` was enabled, the app threw an unhandled exception (Program.cs:32).
  3. Process terminated; container exited.
  4. Container record was removed (runs with `--rm`, cleanup script, or orchestrator removed the failed container).
  5. Operator issued Docker CLI commands against the daemon for `frosty_bell` → daemon responded “No such container”.

- Contributing factors
  - Application behavior: throws unhandled exception when `FAIL_ON_STARTUP` is set.
  - Deployment/runtime: container may be started with `--rm` or orchestrator auto-removes failed containers; lack of persisted logs.
  - Operational: possible queries against wrong host/node; insufficient retention of debug artifacts.

- Confidence
  - Container absence on the queried daemon: High (direct daemon message).
  - Exit + removal due to startup exception: Medium (consistent with earlier logs but not proven by events or timestamps).
  - Alternative causes (querying wrong host, manual removal): Medium — cannot rule out without host/`docker events` output.

- Uncertainties / missing data
  - Whether `frosty_bell` ever existed on this daemon (creation events absent).
  - Exit code, container lifecycle events, timestamps.
  - Whether a different host / orchestrator is responsible (K8s pod vs local docker).
  - Whether `--rm` or automatic cleanup was used.
  - Recommended additional evidence: `docker ps -a`, `docker events --since`, `docker logs --since` (if container id known), `kubectl describe pod` / `kubectl logs --previous` if running under k8s, and `docker inspect` output for the container ID if available.

# 💡 Recommended solution

Immediate mitigation (restore visibility / triage)
1. Search for the container (may be exited or different name):
   ```bash
   docker ps -a --filter "name=frosty_bell"
   docker ps -a | grep frosty_bell -C 5
   ```
2. Inspect daemon events to find create/exit/remove timeline:
   ```bash
   docker events --since "$(date --iso-8601=seconds -d '15 minutes ago')" --until "$(date --iso-8601=seconds)"
   ```
3. If using Kubernetes, check pods and previous logs:
   ```bash
   kubectl get pods -A | grep -i frosty_bell
   kubectl describe pod <pod-name> -n <ns>
   kubectl logs <pod-name> -n <ns> --previous
   ```
4. If container was removed and you need diagnostics, restart it without auto-remove and preserve logs:
   ```bash
   docker run --name frosty_bell_debug -e FAIL_ON_STARTUP=false --restart=unless-stopped -d <image>
   docker logs -f frosty_bell_debug
   ```

Permanent fix (application & deployment)
1. Remove or guard test-only env var:
   - Ensure `FAIL_ON_STARTUP` is not set in production manifests.
2. Harden startup behavior:
   - Replace unhandled throw with controlled check and graceful exit/logging; add top-level try/catch in Program.Main to produce structured diagnostics and predictable exit codes.
3. Avoid destructive runtime flags in prod:
   - Do not use `--rm` for production containers; use restart policies and retain exited containers for post-mortem.

Preventive actions (observability & process)
1. Centralized logs: ship stdout/stderr to a logging backend so short-lived containers’ logs are retained.
2. Monitor & alert: restart counts, CrashLoopBackOff, and “container removed” events.
3. CI/CD gate: disallow test-only env vars in production deployments.
4. Instrument lifecycle: emit startup success/failed events to monitoring with timestamps.

Verification steps (commands)
```bash
# Confirm container/pod presence
docker ps --filter "name=frosty_bell"
kubectl get pods -l app=<label> -n <namespace>

# Confirm FAIL_ON_STARTUP not present in running instance
docker exec -it <container> printenv | grep FAIL_ON_STARTUP || echo "not set"
kubectl exec -it <pod> -n <ns> -- printenv | grep FAIL_ON_STARTUP || echo "not set"
```

If you provide `docker ps -a`, `docker events` output, or `kubectl describe` / `kubectl logs --previous`, I will refine the timeline and recommend the exact corrective action.