[CmdletBinding()]
param(
    [string]$HelmPath = "helm"
)

$ErrorActionPreference = "Stop"

function Invoke-Helm {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & $HelmPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Helm command failed: helm $($Arguments -join ' ')"
    }
}

function Assert-YamlDocuments {
    param([Parameter(Mandatory = $true)][string]$Path)
    $parser = "import sys,yaml; docs=list(yaml.safe_load_all(open(sys.argv[1], encoding='utf-8-sig'))); assert any(doc is not None for doc in docs)"
    & python -c $parser $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Observability rendered invalid YAML."
    }
}

function Assert-TemplateRejected {
    param(
        [Parameter(Mandatory = $true)][string[]]$AdditionalArguments,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )
    $arguments = @(
        "template", "tenantwright-observability-negative", $chart,
        "--namespace", "tenantwright-observability",
        "--values", $productionValues
    ) + $AdditionalArguments
    $priorErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = (& $HelmPath @arguments 2>&1) -join "`n"
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorErrorActionPreference
    }
    if ($exitCode -eq 0 -or $output.IndexOf($ExpectedMessage, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Observability negative render was not rejected as expected: $ExpectedMessage"
    }
}

if (-not (Get-Command $HelmPath -ErrorAction SilentlyContinue)) {
    throw "Required command not found: $HelmPath"
}
if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Required Python YAML validation runtime was not found."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$chart = Join-Path $repoRoot "charts/tenantwright-observability"
$productionValues = Join-Path $chart "ci/production-values.yaml"
$exampleValues = Join-Path $chart "values-production.example.yaml"
$secretExample = Join-Path $repoRoot "charts/environments/production/examples/secrets/observability-secrets.yaml.example"
$installer = Join-Path $repoRoot "charts/helm-install-observability.ps1"
$renderPath = Join-Path ([System.IO.Path]::GetTempPath()) "tenantwright-observability-verify-$PID.yaml"
$preflightValues = Join-Path ([System.IO.Path]::GetTempPath()) "tenantwright-observability-preflight-values-$PID.yaml"

try {
    Invoke-Helm @("dependency", "build", $chart, "--skip-refresh")
    Invoke-Helm @("lint", $chart)
    Invoke-Helm @("lint", $chart, "--values", $productionValues)

    & $HelmPath template tenantwright-observability $chart `
        --namespace tenantwright-observability `
        --values $productionValues | Out-File -LiteralPath $renderPath -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "Production observability render failed."
    }

    $render = Get-Content -LiteralPath $renderPath -Raw
    Assert-YamlDocuments -Path $renderPath

    $secretParser = "import sys,yaml; docs=[d for d in yaml.safe_load_all(open(sys.argv[1], encoding='utf-8-sig')) if d]; actual={d['metadata']['name']:set((d.get('stringData') or d.get('data') or {}).keys()) for d in docs}; expected={'tenantwright-grafana-admin':{'admin-user','admin-password'},'tenantwright-alerting-secrets':{'alertmanager-webhook-url'},'tenantwright-observability-heartbeat':{'webhook-url'},'tenantwright-grafana-oidc':{'client-id','client-secret'},'tenantwright-grafana-database':{'host','name','user','password'},'tenantwright-grafana-database-ca':{'ca.crt'},'tenantwright-prometheus-remote-write':{'token'}}; assert actual==expected"
    & python -c $secretParser $secretExample
    if ($LASTEXITCODE -ne 0) {
        throw "The observability Secret example does not match the required key contract."
    }

    $images = @(
        $render -split "`r?`n" |
            Where-Object { $_ -match '^\s*image:\s*' } |
            ForEach-Object { ($_ -replace '^\s*image:\s*["'']?', '') -replace '["'']?\s*$', '' } |
            Sort-Object -Unique
    )
    $mutableImages = @($images | Where-Object { $_ -notmatch '@sha256:[0-9a-f]{64}$' })
    if ($images.Count -lt 15 -or $mutableImages.Count -gt 0) {
        throw "Production image immutability failed. Mutable images: $($mutableImages -join ', ')"
    }
    if ($render -notmatch '--prometheus-config-reloader=\S+@sha256:[0-9a-f]{64}') {
        throw "Prometheus Operator config-reloader image is not digest pinned."
    }

    foreach ($requiredText in @(
        "kind: Schedule",
        "alert: TenantWrightPrometheusRemoteWriteFailing",
        "alert: TenantWrightTelemetryExporterFailures",
        "alert: TenantWrightObservabilityVolumeLow",
        "alert: Watchdog",
        "name: tenantwright-observability-default-deny",
        "name: tenantwright-observability-https-egress",
        "- tenantwright-observability-heartbeat",
        "credentialsFile: /etc/prometheus/secrets/tenantwright-prometheus-remote-write/token",
        "endpoint: tenantwright-tempo-distributor:4317",
        "endpoint: http://tenantwright-loki-gateway/otlp",
        "url: http://tenantwright-tempo-gateway",
        "url: http://tenantwright-loki-gateway",
        "ssl_mode = verify-full",
        "mountPath: /etc/grafana/database-ca",
        "ingressClassName: nginx-internal",
        "name: `"GF_DATABASE_HOST`"",
        "name: `"GF_AUTH_GENERIC_OAUTH_CLIENT_SECRET`""
    )) {
        if ($render.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw "Production observability render is missing: $requiredText"
        }
    }
    $alertmanagerParser = "import sys,yaml,base64; docs=yaml.safe_load_all(open(sys.argv[1], encoding='utf-8-sig')); values=[base64.b64decode(doc['data']['alertmanager.yaml']).decode() for doc in docs if isinstance(doc,dict) and doc.get('kind')=='Secret' and 'alertmanager.yaml' in doc.get('data',{})]; assert len(values)==1; print(values[0])"
    $alertmanagerConfig = (& python -c $alertmanagerParser $renderPath) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Rendered Alertmanager configuration could not be decoded."
    }
    foreach ($requiredText in @(
        'alertname="Watchdog"',
        "url_file: /etc/alertmanager/secrets/tenantwright-observability-heartbeat/webhook-url",
        "url_file: /etc/alertmanager/secrets/tenantwright-alerting-secrets/alertmanager-webhook-url"
    )) {
        if ($alertmanagerConfig.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw "Rendered Alertmanager configuration is missing: $requiredText"
        }
    }
    if ($render -match '(?i)\.dev\.|letsencrypt-staging|postgres(?:ql)?://|^\s*(?:password|client-secret|token|webhook-url):\s+\S+') {
        throw "Production observability render contains development or inline-secret configuration."
    }

    Assert-TemplateRejected `
        -AdditionalArguments @("--set", "tempo-distributed.enabled=false") `
        -ExpectedMessage "tempo-distributed must be enabled"
    Assert-TemplateRejected `
        -AdditionalArguments @("--set", "loki.ingester.replicas=1") `
        -ExpectedMessage "three ingesters"
    Assert-TemplateRejected `
        -AdditionalArguments @("--set-string", "production.network.objectStorageCidrs[0]=0.0.0.0/0") `
        -ExpectedMessage "entire Internet"

    $exampleRejected = $false
    try {
        & $installer -EnvironmentValuesPath $exampleValues -PreflightOnly *> $null
    }
    catch {
        $exampleRejected = $_.Exception.Message -match "placeholder"
    }
    if (-not $exampleRejected) {
        throw "The production example with unresolved placeholders was not rejected."
    }

    $preflightContent = Get-Content -LiteralPath $productionValues -Raw
    $preflightContent = $preflightContent `
        -replace 'environment: ci', 'environment: production' `
        -replace '\.ci\.invalid', '.production.internal' `
        -replace 'tenantwright\.example', 'tenantwright.internal'
    Set-Content -LiteralPath $preflightValues -Value $preflightContent -Encoding utf8
    & $installer -EnvironmentValuesPath $preflightValues -PreflightOnly
    if ($LASTEXITCODE -ne 0) {
        throw "The secret-safe production installer preflight failed."
    }
}
finally {
    Remove-Item -LiteralPath $renderPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $preflightValues -Force -ErrorAction SilentlyContinue
}

Write-Host "Observability production chart, failure gates, immutable images, and installer preflight passed." -ForegroundColor Green
