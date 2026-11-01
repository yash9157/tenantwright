[CmdletBinding()]
param([string]$HelmPath = "helm")

$ErrorActionPreference = "Stop"
$chartPath = Join-Path $PSScriptRoot "tenantwright-telemetry"
if (-not (Get-Command $HelmPath -ErrorAction SilentlyContinue)) { throw "Required command not found: $HelmPath" }

& $HelmPath lint --strict $chartPath
if ($LASTEXITCODE -ne 0) { throw "Telemetry chart lint failed." }
$defaultRendered = (& $HelmPath template tenantwright-telemetry $chartPath --namespace tenantwright-telemetry) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Telemetry chart render failed." }

foreach ($required in @(
    "kind: DaemonSet", "name: tenantwright-fluent-bit", "name: tenantwright-otel-collector",
    "name: tenantwright-prometheus", "kind: NetworkPolicy",
    "kind: PodDisruptionBudget", "file_storage", "transform/redact", "tenantwright-opensearch-credentials"
)) {
    if ($defaultRendered -notmatch [regex]::Escape($required)) { throw "Default chart is missing required marker: $required" }
}

foreach ($required in @(
    "name: tenantwright-prometheus", "kind: Ingress",
    "api.dev.example.com", 'path: "/prometheus"',
    "nginx.ingress.kubernetes.io/auth-type: basic",
    "tenantwright-prometheus-basic-auth", "letsencrypt-prod",
    "name: tenantwright-prometheus-ingress", "port: 9090",
    "--web.route-prefix=/prometheus"
)) {
    if ($defaultRendered -notmatch [regex]::Escape($required)) { throw "Prometheus data-source boundary is missing required marker: $required" }
}

foreach ($required in @(
    "name: tenantwright-data-prepper", "pipelines.yaml", "entry-pipeline:",
    "raw-trace-pipeline:", "service-map-pipeline:", "otel_traces:",
    "service_map:", "index_type: trace-analytics-raw",
    "index_type: trace-analytics-service-map", "endpoint: tenantwright-data-prepper:21890",
    "checksum/config:", "name: materialize-dataprepper-config",
    "secretName: tenantwright-opensearch-credentials"
)) {
    if ($defaultRendered -notmatch [regex]::Escape($required)) { throw "Trace Analytics chart is missing required marker: $required" }
}

$alternatePort = 21443
$alternatePortRendered = (& $HelmPath template tenantwright-telemetry $chartPath --namespace tenantwright-telemetry --set "opensearch.port=$alternatePort") -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Telemetry chart alternate OpenSearch port render failed." }
if ($alternatePortRendered -notmatch [regex]::Escape("ports: [{ protocol: TCP, port: $alternatePort }]")) {
    throw "Telemetry NetworkPolicy does not follow the configured OpenSearch port."
}

$fallbackRendered = (& $HelmPath template tenantwright-telemetry $chartPath --namespace tenantwright-telemetry --set dataPrepper.enabled=false) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Telemetry chart direct-export fallback render failed." }
if ($fallbackRendered -match [regex]::Escape("name: tenantwright-data-prepper")) {
    throw "Direct-export fallback unexpectedly contains Data Prepper."
}

foreach ($rendered in @($defaultRendered, $fallbackRendered)) {
    foreach ($forbidden in @("tenantwright-observability", "workflows.example.com", "AVNS_", "AccountKey=", "prometheus.auth.password")) {
        if ($rendered -match [regex]::Escape($forbidden)) { throw "Rendered chart contains forbidden marker: $forbidden" }
    }
    if ($rendered -match '(?im)^\s*password:\s+[A-Za-z0-9+/=]{20,}\s*$') { throw "Rendered chart contains an inline password." }
}
Write-Host "Telemetry chart verification passed." -ForegroundColor Green
