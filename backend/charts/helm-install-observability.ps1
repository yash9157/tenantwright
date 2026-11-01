[CmdletBinding()]
param(
    [string]$Namespace = "tenantwright-observability",
    [string]$EnvironmentValuesPath = "",
    [string]$HelmPath = "helm",
    [string]$KubectlPath = "kubectl",
    [switch]$CreateNamespace,
    [switch]$PreflightOnly,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if ($PreflightOnly -and $DryRun) {
    throw "-PreflightOnly and -DryRun are separate modes."
}

function Assert-CommandExists {
    param([Parameter(Mandatory = $true)][string]$CommandName)
    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $CommandName"
    }
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Assert-SecretKey {
    param(
        [Parameter(Mandatory = $true)][string]$SecretName,
        [Parameter(Mandatory = $true)][string]$Key
    )
    $template = "{{if index .data `"$Key`"}}present{{end}}"
    $result = (& $KubectlPath -n $Namespace get secret $SecretName -o "go-template=$template" 2>$null) -join ""
    if ($LASTEXITCODE -ne 0 -or $result -ne "present") {
        throw "Required Secret '$SecretName' is absent or does not contain key '$Key' in namespace '$Namespace'."
    }
}

function Assert-ProductionImagesImmutable {
    param([Parameter(Mandatory = $true)][string]$Rendered)
    $mutableImages = @(
        $Rendered -split "`r?`n" |
            Where-Object { $_ -match '^\s*image:\s*' } |
            ForEach-Object { ($_ -replace '^\s*image:\s*["'']?', '') -replace '["'']?\s*$', '' } |
            Where-Object { $_ -notmatch '@sha256:[0-9a-f]{64}$' } |
            Sort-Object -Unique
    )
    $reloaderReferences = @(
        [regex]::Matches($Rendered, '--prometheus-config-reloader=(?<image>\S+)') |
            ForEach-Object { $_.Groups["image"].Value } |
            Sort-Object -Unique
    )
    $mutableImages += @(
        $reloaderReferences |
            Where-Object { $_ -notmatch '@sha256:[0-9a-f]{64}$' }
    )
    if ($reloaderReferences.Count -eq 0) {
        $mutableImages += "missing prometheus-config-reloader reference"
    }
    if ($mutableImages.Count -gt 0) {
        throw "Production observability render contains mutable images: $($mutableImages -join ', ')"
    }
}

$chartPath = Join-Path $PSScriptRoot "tenantwright-observability"
$resolvedValuesPath = ""
Assert-CommandExists -CommandName $HelmPath

if (-not [string]::IsNullOrWhiteSpace($EnvironmentValuesPath)) {
    $resolvedValuesPath = [System.IO.Path]::GetFullPath($EnvironmentValuesPath)
    if (-not (Test-Path -LiteralPath $resolvedValuesPath -PathType Leaf)) {
        throw "Observability environment values file not found: $resolvedValuesPath"
    }
    $rawValues = Get-Content -LiteralPath $resolvedValuesPath -Raw
    if ($rawValues -match '(?im)^\s*(?:password|client-secret|token|webhook-url|access-key|secret-key)\s*:\s*\S+') {
        throw "Observability environment values must contain Secret references only, never secret values."
    }
    if ($rawValues -match '(?i)<[^>]+>|change[_-]?me|(?:^|[./])(?:example|test|ci)\.invalid(?:[:/]|$)|\.example(?:[:/]|$)') {
        throw "Observability environment values contain a placeholder token."
    }
}
elseif (-not $DryRun) {
    throw "Live deployment and production preflight require -EnvironmentValuesPath."
}

Invoke-CheckedCommand -Command $HelmPath `
    -Arguments @("dependency", "build", $chartPath, "--skip-refresh") `
    -FailureMessage "Observability Helm dependency build failed."

$valueArguments = if ($resolvedValuesPath) { @("--values", $resolvedValuesPath) } else { @() }
Invoke-CheckedCommand -Command $HelmPath -Arguments (@("lint", $chartPath) + $valueArguments) `
    -FailureMessage "Observability Helm lint failed."

$renderPath = Join-Path ([System.IO.Path]::GetTempPath()) "tenantwright-observability-preflight-$PID.yaml"
try {
    $renderArguments = @("template", "tenantwright-observability-preflight", $chartPath, "--namespace", $Namespace) + $valueArguments
    & $HelmPath @renderArguments | Out-File -LiteralPath $renderPath -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "Observability production rendering failed."
    }

    $rendered = Get-Content -LiteralPath $renderPath -Raw
    if ($resolvedValuesPath) {
        if ($rendered -notmatch '(?m)^kind:\s+Schedule\s*$' -or
            $rendered -notmatch '(?m)^\s*name:\s+tenantwright-observability-default-deny\s*$') {
            throw "Environment deployment must enable the production observability profile."
        }
        Assert-ProductionImagesImmutable -Rendered $rendered
        if ($rendered -match '(?im)\.dev\.|letsencrypt-staging|127\.0\.0\.1:65535|^\s*password:\s+\S+') {
            throw "Production observability render contains development or inline-secret configuration."
        }
    }

    if ($DryRun) {
        Get-Content -LiteralPath $renderPath
        return
    }
    if ($PreflightOnly) {
        Write-Host "Observability production preflight passed. No cluster changes were made." -ForegroundColor Green
        return
    }

    Assert-CommandExists -CommandName $KubectlPath
    if ($CreateNamespace) {
        & $KubectlPath get namespace $Namespace *> $null
        if ($LASTEXITCODE -ne 0) {
            Invoke-CheckedCommand -Command $KubectlPath -Arguments @("create", "namespace", $Namespace) `
                -FailureMessage "Failed to create observability namespace '$Namespace'."
        }
    }
    Invoke-CheckedCommand -Command $KubectlPath -Arguments @("get", "namespace", $Namespace, "-o", "name") `
        -FailureMessage "Observability namespace '$Namespace' does not exist."

    foreach ($crd in @(
        "prometheuses.monitoring.coreos.com",
        "alertmanagers.monitoring.coreos.com",
        "servicemonitors.monitoring.coreos.com",
        "prometheusrules.monitoring.coreos.com",
        "schedules.velero.io"
    )) {
        Invoke-CheckedCommand -Command $KubectlPath `
            -Arguments @("get", "customresourcedefinition", $crd, "-o", "name") `
            -FailureMessage "Required production observability CRD is not installed: $crd"
    }

    foreach ($requirement in @(
        @("tenantwright-grafana-admin", "admin-user"),
        @("tenantwright-grafana-admin", "admin-password"),
        @("tenantwright-grafana-oidc", "client-id"),
        @("tenantwright-grafana-oidc", "client-secret"),
        @("tenantwright-grafana-database", "host"),
        @("tenantwright-grafana-database", "name"),
        @("tenantwright-grafana-database", "user"),
        @("tenantwright-grafana-database", "password"),
        @("tenantwright-grafana-database-ca", "ca.crt"),
        @("tenantwright-alerting-secrets", "alertmanager-webhook-url"),
        @("tenantwright-observability-heartbeat", "webhook-url"),
        @("tenantwright-prometheus-remote-write", "token")
    )) {
        Assert-SecretKey -SecretName $requirement[0] -Key $requirement[1]
    }

    $upgradeArguments = @(
        "upgrade", "--install", "tenantwright-observability", $chartPath,
        "--namespace", $Namespace,
        "--values", $resolvedValuesPath,
        "--atomic", "--wait", "--history-max", "10", "--timeout", "20m"
    )
    Invoke-CheckedCommand -Command $HelmPath -Arguments $upgradeArguments `
        -FailureMessage "Observability Helm install/upgrade failed and was rolled back."
}
finally {
    Remove-Item -LiteralPath $renderPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Observability release is installed in namespace '$Namespace'." -ForegroundColor Green
