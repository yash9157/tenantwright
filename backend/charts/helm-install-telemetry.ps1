[CmdletBinding()]
param(
    [string]$Namespace = "tenantwright-telemetry",
    [string]$HelmPath = "helm",
    [string]$KubectlPath = "kubectl",
    [switch]$CreateNamespace,
    [switch]$PreflightOnly,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$chartPath = Join-Path $PSScriptRoot "tenantwright-telemetry"

function Assert-CommandExists([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "Required command not found: $Name" }
}

function Assert-SecretKey([string]$Name, [string]$Key) {
    $jsonPath = "jsonpath={.data.$Key}"
    $result = (& $KubectlPath -n $Namespace get secret $Name -o $jsonPath 2>$null) -join ""
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($result)) {
        throw "Required Secret '$Name' is absent or missing key '$Key' in namespace '$Namespace'."
    }
}

Assert-CommandExists $HelmPath
if (-not $DryRun) { Assert-CommandExists $KubectlPath }

& $HelmPath lint $chartPath
if ($LASTEXITCODE -ne 0) { throw "Telemetry Helm lint failed." }

$renderPath = Join-Path ([System.IO.Path]::GetTempPath()) "tenantwright-telemetry-preflight-$PID.yaml"
try {
    & $HelmPath template tenantwright-telemetry-preflight $chartPath --namespace $Namespace | Out-File $renderPath -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Telemetry Helm rendering failed." }
    $rendered = Get-Content $renderPath -Raw
    if ($rendered -match '(?i)AVNS_|AccountKey=|password:\s+[A-Za-z0-9+/=]{20,}') { throw "Rendered telemetry manifests contain inline credentials." }
    if ($PreflightOnly -or $DryRun) { Write-Host "Telemetry chart preflight passed. No cluster changes were made." -ForegroundColor Green; return }

    if ($CreateNamespace) {
        & $KubectlPath get namespace $Namespace *> $null
        if ($LASTEXITCODE -ne 0) { & $KubectlPath create namespace $Namespace | Out-Host }
    }
    & $KubectlPath get namespace $Namespace -o name | Out-Null
    Assert-SecretKey "tenantwright-opensearch-credentials" "username"
    Assert-SecretKey "tenantwright-opensearch-credentials" "password"

    & $HelmPath upgrade --install tenantwright-telemetry $chartPath --namespace $Namespace --create-namespace --rollback-on-failure --wait --timeout 20m --history-max 10
    if ($LASTEXITCODE -ne 0) { throw "Telemetry Helm install failed and was rolled back." }
    Write-Host "Telemetry release is deployed in namespace '$Namespace'." -ForegroundColor Green
}
finally { Remove-Item $renderPath -Force -ErrorAction SilentlyContinue }
