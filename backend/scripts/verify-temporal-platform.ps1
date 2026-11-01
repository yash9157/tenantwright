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
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Owner
    )
    $documentCount = 0
    foreach ($document in ($Content -split '(?m)^---\s*$')) {
        if ([string]::IsNullOrWhiteSpace($document)) {
            continue
        }
        $documentCount++
        try {
            $null = $document | ConvertFrom-Yaml -ErrorAction Stop
        }
        catch {
            throw "$Owner rendered invalid YAML document $documentCount. $($_.Exception.Message)"
        }
    }
    if ($documentCount -eq 0) {
        throw "$Owner rendered no YAML documents."
    }
}

function Assert-HelmTemplateRejected {
    param(
        [Parameter(Mandatory = $true)][string[]]$AdditionalArguments,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )
    $arguments = @(
        "template", "tenantwright-temporal-negative", $temporalChart,
        "--namespace", "tenantwright-temporal",
        "--values", $productionValues
    ) + $AdditionalArguments
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = (& $HelmPath @arguments 2>&1) -join "`n"
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($LASTEXITCODE -eq 0 -or $output.IndexOf($ExpectedMessage, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Temporal negative render was not rejected as expected: $ExpectedMessage"
    }
}

if (-not (Get-Command $HelmPath -ErrorAction SilentlyContinue)) {
    throw "Required command not found: $HelmPath"
}
if (-not (Get-Command ConvertFrom-Yaml -ErrorAction SilentlyContinue)) {
    throw "Required ConvertFrom-Yaml command was not found."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$temporalChart = Join-Path $repoRoot "charts/tenantwright-temporal"
$observabilityChart = Join-Path $repoRoot "charts/tenantwright-observability"
$productionValues = Join-Path $temporalChart "ci/production-values.yaml"
$exampleValues = Join-Path $repoRoot "charts/environments/production/platform/temporal.values.yaml.example"
$installer = Join-Path $repoRoot "charts/helm-install-temporal.ps1"
$renderedTemporal = Join-Path ([System.IO.Path]::GetTempPath()) "tenantwright-temporal-verify-$PID.yaml"
$renderedObservability = Join-Path ([System.IO.Path]::GetTempPath()) "tenantwright-observability-temporal-verify-$PID.yaml"

try {
    Invoke-Helm @("dependency", "build", $temporalChart, "--skip-refresh")
    Invoke-Helm @("lint", $temporalChart)
    Invoke-Helm @("lint", $temporalChart, "--values", $productionValues)

    & $HelmPath template tenantwright-temporal $temporalChart --namespace tenantwright-temporal `
        --values $productionValues | Out-File -LiteralPath $renderedTemporal -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "Production Temporal render failed."
    }

    $temporal = Get-Content -LiteralPath $renderedTemporal -Raw
    Assert-YamlDocuments -Content $temporal -Owner "Temporal"
    foreach ($digest in @(
        "sha256:c3e752127759616bb1615e0f9ba0e21635aeb5fdeb922de4f371c350955f46ae",
        "sha256:a9f84fb9a374b2374fe2e67c8efc0468ff3f1c66c8a0b14597ec86e349e62bca",
        "sha256:3899d5cf749b5555c9f798d46f2423b1d6190ea391ca713db5d1bba961759e58"
    )) {
        if ($temporal.IndexOf("@$digest", [StringComparison]::Ordinal) -lt 0) {
            throw "Production Temporal render is missing approved digest $digest."
        }
    }
    foreach ($requiredText in @(
        "automountServiceAccountToken: false",
        "readOnlyRootFilesystem: true",
        "enableHostVerification: true",
        "kubernetes.io/metadata.name: `"tenantwright`"",
        "cidr: `"10.20.30.40/32`"",
        "name: tenantwright-temporal-schema-management"
    )) {
        if ($temporal.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw "Production Temporal render is missing required hardening: $requiredText"
        }
    }
    if ($temporal -match '(?i)postgres(?:ql)?://') {
        throw "Production Temporal render contains an inline PostgreSQL URI."
    }

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
    Assert-HelmTemplateRejected `
        -AdditionalArguments @("--set-string", "temporal.server.image.digest=mutable") `
        -ExpectedMessage "digest"
    Assert-HelmTemplateRejected `
        -AdditionalArguments @("--set", "temporal.server.config.persistence.datastores.default.sql.tls.enableHostVerification=false") `
        -ExpectedMessage "value must be true"
    Assert-HelmTemplateRejected `
        -AdditionalArguments @("--set-string", "temporal.server.config.persistence.datastores.visibility.sql.databaseName=temporal") `
        -ExpectedMessage "separate databases"
    Assert-HelmTemplateRejected `
        -AdditionalArguments @("--set-string", "networkPolicy.postgresqlCidrs[0]=0.0.0.0/0") `
        -ExpectedMessage "internet-wide"

    Invoke-Helm @("lint", $observabilityChart)
    & $HelmPath template tenantwright-observability $observabilityChart `
        --namespace tenantwright-observability | Out-File -LiteralPath $renderedObservability -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "Temporal observability render failed."
    }
    $observability = Get-Content -LiteralPath $renderedObservability -Raw
    foreach ($requiredText in @(
        "alert: TenantWrightTemporalDeploymentUnavailable",
        "alert: TenantWrightTemporalPersistenceErrors",
        "alert: TenantWrightTemporalServiceErrorRatio",
        "name: tenantwright-temporal-dashboard"
    )) {
        if ($observability.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw "Temporal observability render is missing: $requiredText"
        }
    }
    foreach ($template in @(
        "templates/temporal-prometheus-rules.yaml",
        "templates/temporal-grafana-dashboard.yaml"
    )) {
        $ownedRender = (& $HelmPath template tenantwright-observability $observabilityChart `
            --namespace tenantwright-observability --show-only $template) -join "`n"
        if ($LASTEXITCODE -ne 0) {
            throw "Temporal observability template failed to render: $template"
        }
        Assert-YamlDocuments -Content $ownedRender -Owner $template
    }
}
finally {
    Remove-Item -LiteralPath $renderedTemporal -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $renderedObservability -Force -ErrorAction SilentlyContinue
}

Write-Host "Temporal platform chart, production gates, and observability verification passed." -ForegroundColor Green
