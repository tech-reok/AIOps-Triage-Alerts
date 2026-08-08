# 🚨 Detected fail

- What failed (concise): HTTPS redirection could not determine an HTTPS port; application bound only to HTTP on port 8080.
- Affected container/service: csharp-api-prod (CsharpAppBuildDocker.Api)
- Relevant log excerpts:
  - warn: Microsoft.AspNetCore.Hosting.Diagnostics[15]  
    Overriding HTTP_PORTS '8080' and HTTPS_PORTS ''. Binding to values defined by URLS instead 'http://+:8080'.
  - info: Microsoft.Hosting.Lifetime[14]  
    Now listening on: http://[::]:8080
  - info: Microsoft.Hosting.Lifetime[0]  
    Application started. Press Ctrl+C to shut down.
  - warn: Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3]  
    Failed to determine the https port for redirect.
- Exit code / signal: none (application started and is running)
- Timestamps / sequence: not provided in the logs; sequence (binding → listening → started → https-redirection warning) is implied by log order.
- Impact:
  - Confirmed: app is running and listening on HTTP port 8080.
  - Likely: requests to HTTP will not be automatically redirected to HTTPS by the app (no in-process HTTPS port known), so either:
    - clients remain on HTTP, or
    - TLS termination/reverse-proxy must enforce HTTPS.
  - No crash or outage from these logs; the messages are warnings, not fatal errors.

# 🔍 Root cause analisys (RCA)

- Root cause (most likely)
  - The application is configured/bound only to an HTTP URL (http://+:8080) while HTTPS port configuration is absent (HTTPS_PORTS is empty). ASP.NET Core's HttpsRedirectionMiddleware cannot infer an HTTPS port and logs the warning.
- Evidence (from supplied logs)
  - "Binding to values defined by URLS instead 'http://+:8080'." — confirms the server address is HTTP-only.
  - "Now listening on: http://[::]:8080" + "Application started." — confirms successful HTTP bind.
  - "Failed to determine the https port for redirect." — direct symptom from HttpsRedirectionMiddleware.
- Failure chain (step-by-step)
  1. Startup reads environment/deployment variables: HTTP_PORTS='8080', HTTPS_PORTS='' (empty) and/or ASPNETCORE_URLS set to 'http://+:8080'.
  2. Kestrel/hosting binds to the HTTP endpoint http://+:8080.
  3. Application starts successfully and begins serving HTTP traffic.
  4. HttpsRedirectionMiddleware runs on startup or first request and attempts to determine an HTTPS port to perform redirects.
  5. Because no HTTPS port is configured/advertised to the host process, the middleware logs "Failed to determine the https port for redirect" and will not perform automatic HTTP→HTTPS redirection.
- Contributing factors
  - Configuration: HTTPS_PORTS env var empty; URLS configured to HTTP only.
  - Deployment topology ambiguity: TLS may be terminated upstream (load balancer / ingress), and app may not need to host HTTPS itself — but middleware is enabled regardless.
  - Missing orchestration settings or forwarded headers that would allow the middleware to infer secure scheme (if TLS terminated upstream).
- Confidence: High that the cause is configuration (HTTP-only binding + missing HTTPS port).  
- Uncertainty (not determinable from logs)
  - Whether TLS termination is expected to be handled by a reverse proxy/load balancer (and thus whether this warning is acceptable).
  - Exact environment variables in the runtime (full env not provided).
  - Whether HttpsRedirectionMiddleware is intentionally enabled in code or via configuration.

# 💡 Recommended solution

Prioritize actions by impact and permanence.

1) Immediate mitigation
- If TLS is terminated upstream (recommended in containers), disable in-app HTTPS redirection OR configure forwarded headers so redirection is unnecessary.
  - Option A (disable redirection at runtime): remove/disable HttpsRedirectionMiddleware in Startup/Program or set config flag (e.g., in appsettings or env var) to disable it.
  - Option B (allow redirect): set an HTTPS port or advertise it via env var and/or configure Kestrel to listen on HTTPS.
- Quick check commands:
  ```bash
  # Inspect env vars in running container (k8s example)
  kubectl exec -it <pod> -n <ns> -- printenv | egrep "HTTP_PORTS|HTTPS_PORTS|ASPNETCORE_URLS|ASPNETCORE_HTTPS_PORT" -n

  # Check if app is reachable on HTTP
  curl -v http://<host-or-pod-ip>:8080/health
  ```
2) Permanent fixes
- If app must redirect HTTP→HTTPS and host TLS inside the app:
  - Configure HTTPS binding and certificate in Kestrel (or set ASPNETCORE_HTTPS_PORT):
    - Example Docker/K8s env change:
      - Set ASPNETCORE_URLS to include https (e.g., "http://+:8080;https://+:8443") and provide certs.
      - Or set ASPNETCORE_HTTPS_PORT to the HTTPS port number for HttpsRedirection to pick up.
  - In code, set https port explicitly for redirection:
    ```csharp
    // Program.cs / Startup
    app.UseHttpsRedirection(new HttpsRedirectionOptions { HttpsPort = 8443 });
    ```
- If TLS is terminated upstream (recommended):
  - Keep the app HTTP-only, but:
    - Disable HttpsRedirectionMiddleware or guard it by environment (only enable in environments where the app hosts TLS).
    - Ensure UseForwardedHeaders is configured so app sees X-Forwarded-Proto and does not attempt redirects:
      ```csharp
      app.UseForwardedHeaders(new ForwardedHeadersOptions {
          ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor
      });
      ```
    - Ensure the ingress/load balancer sets X-Forwarded-Proto and that redirect is handled at that layer.
- Update deployment manifests to avoid conflicting HTTP_PORTS/HTTPS_PORTS and to use ASPNETCORE_URLS consistently.

3) Preventive actions
- Add startup-time health check or config validation: log effective binding URLs and whether HTTPS redirection is active and usable.
- Document intended TLS topology (in-app vs. external) and enforce via CI/CD checks that prod manifests do not include test-only flags or missing cert configs.
- Add an alert for repeated "Failed to determine the https port for redirect" warnings if redirection is expected.

4) Verification steps
- After change, confirm no warning appears and redirection behavior is correct:
  ```bash
  # Check logs for absence of the warning
  kubectl logs <pod> -n <ns> --tail=200 | grep "Failed to determine the https port" || echo "no https redirect warning"

  # Verify redirect behavior (if app should redirect)
  curl -v http://<host>:8080/ -I
  # Expect 301/302 Location: https://...
  ```
- If TLS is external, verify ingress enforces HTTPS:
  ```bash
  # Expect HTTP requests redirected by ingress
  curl -v http://<public-host>/ -I
  ```

Notes (do not assume): if TLS is intended to be handled by a proxy (common in container deployments), the warning is informational and can be addressed either by disabling in-app redirection or by configuring forwarded headers. If you want, provide the pod's environment (`printenv`), the Program.cs startup snippet around middleware registration, and the deployment/ingress manifest and I will give precise edit suggestions.