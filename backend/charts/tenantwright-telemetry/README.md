# TenantWright telemetry

This chart is the dev/prod-shaped OpenSearch pipeline: Fluent Bit tails
Kubernetes container logs, the OpenTelemetry Collector receives OTLP
logs/traces/metrics, logs go directly to managed OpenSearch, Data Prepper converts
traces into the native Trace Analytics span and service-map indices, and the
Collector exposes Prometheus-format metrics on port 8889.

Create the `tenantwright-opensearch-credentials` Secret in `tenantwright-telemetry` with `username` and `password` keys. Keep it in an ignored local file or create it directly with `kubectl`; never put credentials in values or Git. Set `opensearch.host` and `opensearch.port` to the managed OpenSearch API endpoint, not the Dashboards endpoint.

Use `charts/environments/production/examples/secrets/opensearch-credentials.yaml.example` as the structure for an operator-owned Secret file. The populated file must remain outside version control.

For rotation, copy only the new password to the local clipboard and run the
guarded operator script. It writes the ignored manifest and applies the Secret
without placing the password in shell history or command output:

```powershell
.\charts\rotate-opensearch-credentials.ps1 `
  -Username avnadmin `
  -PasswordFromClipboard `
  -Apply
```

Redeploy the complete chart after changing the endpoint or credential so the
Secret-consuming workloads restart and the NetworkPolicy follows the configured
OpenSearch API port.

Validate before deployment:

```powershell
helm lint .
helm template tenantwright-telemetry . --namespace tenantwright-telemetry --set opensearch.host=example.invalid
```

Deploy only after the Secret exists and the OpenSearch endpoint configured in
`values.yaml` is reachable. Data Prepper is enabled by default because it owns
the Trace Analytics document
model. The chart intentionally does not install Loki, Tempo, Grafana, Promtail,
or an observability namespace from the legacy bundle.

The OpenSearch Dashboards Metrics source uses the HTTPS Prometheus path declared
under `prometheus.ingress`. Create the referenced NGINX Basic Auth Secret before
deployment. The Secret must contain an `auth` htpasswd entry for ingress-nginx
and the same plaintext `username` and `password` values for operator-managed
OpenSearch data-source registration. Never commit those values.

The guarded installer performs lint/render/inline-secret checks and verifies both Secret keys before any live change:

```powershell
.\backend\charts\helm-install-telemetry.ps1 -PreflightOnly
.\backend\charts\helm-install-telemetry.ps1 -CreateNamespace
```
