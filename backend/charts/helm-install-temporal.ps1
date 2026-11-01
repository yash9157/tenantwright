[CmdletBinding()]
param(
    [ValidateSet("dev", "production")][string]$Environment = "production",
    [string]$Namespace = "tenantwright-temporal",
    [string]$ReleaseName = "tenantwright-temporal",
    [string]$EnvironmentValuesPath = "",
    [string]$HelmPath = "helm",
    [string]$KubectlPath = "kubectl",
    [switch]$CreateNamespace,
    [switch]$PreflightOnly,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'environment-values.ps1')

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
    $secretJson = (& $KubectlPath -n $Namespace get secret $SecretName -o json 2>$null) -join ""
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($secretJson)) {
        throw "Required Secret '$SecretName' is absent or does not contain key '$Key' in namespace '$Namespace'."
    }
    $secret = $secretJson | ConvertFrom-Json
    $keyNames = @($secret.data.PSObject.Properties.Name)
    if ($keyNames -notcontains $Key) {
        throw "Required Secret '$SecretName' is absent or does not contain key '$Key' in namespace '$Namespace'."
    }
}

$chartPath = Join-Path $PSScriptRoot "tenantwright-temporal"
$resolvedValuesPath = ""
Assert-CommandExists -CommandName $HelmPath

$resolvedValuesPaths = @()
if ([string]::IsNullOrWhiteSpace($EnvironmentValuesPath) -and $Environment -eq 'dev') {
    $resolvedValuesPaths = @(Get-EnvironmentValuesFiles -EnvironmentRoot (Join-Path $PSScriptRoot 'environments/dev') -ReleaseName $ReleaseName -Platform)
}
elseif (-not [string]::IsNullOrWhiteSpace($EnvironmentValuesPath)) {
    $resolvedValuesPath = [System.IO.Path]::GetFullPath($EnvironmentValuesPath)
    if (-not (Test-Path -LiteralPath $resolvedValuesPath -PathType Leaf)) {
        throw "Temporal environment values file not found: $resolvedValuesPath"
    }
    $resolvedValuesPaths = @($resolvedValuesPath)
}
elseif (-not $DryRun) {
    throw "Production deployment and preflight require -EnvironmentValuesPath."
}
foreach ($path in $resolvedValuesPaths) {
    $rawValues = Get-Content -LiteralPath $path -Raw
    if ($rawValues -match '(?i)postgres(?:ql)?://|(?m)^\s*(?:password|api-key|client-secret)\s*:\s*\S+') {
        throw "Temporal environment values must contain Secret references only, never credentials or connection URIs."
    }
    if ($rawValues -match '(?i)<[^>]+>|change[_-]?me|example\.invalid|\.example(?:[:/]|$)') {
        throw "Temporal environment values contain a placeholder token."
    }
}

Invoke-CheckedCommand -Command $HelmPath -Arguments @("dependency", "build", $chartPath, "--skip-refresh") `
    -FailureMessage "Temporal Helm dependency build failed."

$valueArguments = @()
foreach ($path in $resolvedValuesPaths) { $valueArguments += @('--values', $path) }
Invoke-CheckedCommand -Command $HelmPath -Arguments (@("lint", $chartPath) + $valueArguments) `
    -FailureMessage "Temporal Helm lint failed."

$renderPath = Join-Path ([System.IO.Path]::GetTempPath()) "$ReleaseName-preflight-$PID.yaml"
try {
    $renderArguments = @("template", "$ReleaseName-preflight", $chartPath, "--namespace", $Namespace) + $valueArguments
    & $HelmPath @renderArguments | Out-File -LiteralPath $renderPath -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "Temporal production rendering failed."
    }

    $rendered = Get-Content -LiteralPath $renderPath -Raw
    if ($rendered -match '(?i)postgres(?:ql)?://') {
        throw "Rendered Temporal manifests contain inline database credentials."
    }
    $requiredDigests = [System.Collections.Generic.List[string]]@(
        "sha256:c3e752127759616bb1615e0f9ba0e21635aeb5fdeb922de4f371c350955f46ae",
        "sha256:a9f84fb9a374b2374fe2e67c8efc0468ff3f1c66c8a0b14597ec86e349e62bca"
    )
    if ($rendered -match [regex]::Escape("temporalio/ui@")) {
        $requiredDigests.Add("sha256:3899d5cf749b5555c9f798d46f2423b1d6190ea391ca713db5d1bba961759e58")
    }
    foreach ($digest in $requiredDigests) {
        if ($rendered.IndexOf("@$digest", [StringComparison]::Ordinal) -lt 0) {
            throw "Rendered Temporal manifests do not contain every approved immutable image digest."
        }
    }

    if ($DryRun) {
        Get-Content -LiteralPath $renderPath
        return
    }
    if ($PreflightOnly) {
        Write-Host "Temporal deployment preflight passed. No cluster changes were made." -ForegroundColor Green
        return
    }

    Assert-CommandExists -CommandName $KubectlPath
    if ($CreateNamespace) {
        & $KubectlPath get namespace $Namespace *> $null
        if ($LASTEXITCODE -ne 0) {
            Invoke-CheckedCommand -Command $KubectlPath -Arguments @("create", "namespace", $Namespace) `
                -FailureMessage "Failed to create Temporal namespace '$Namespace'."
        }
    }
    Invoke-CheckedCommand -Command $KubectlPath -Arguments @("get", "namespace", $Namespace, "-o", "name") `
        -FailureMessage "Temporal namespace '$Namespace' does not exist."

    foreach ($requirement in @(
        @("tenantwright-temporal-persistence", "password"),
        @("tenantwright-temporal-visibility", "password"),
        @("tenantwright-temporal-persistence-schema", "password"),
        @("tenantwright-temporal-visibility-schema", "password"),
        @("tenantwright-temporal-postgresql-ca", "ca.crt"),
        @("tenantwright-temporal-internode-tls", "ca.crt"),
        @("tenantwright-temporal-internode-tls", "tls.crt"),
        @("tenantwright-temporal-internode-tls", "tls.key"),
        @("tenantwright-temporal-frontend-tls", "ca.crt"),
        @("tenantwright-temporal-frontend-tls", "tls.crt"),
        @("tenantwright-temporal-frontend-tls", "tls.key"),
        @("tenantwright-temporal-sdk-client-ca", "ca.crt"),
        @("tenantwright-temporal-web-client-tls", "ca.crt"),
        @("tenantwright-temporal-web-client-tls", "tls.crt"),
        @("tenantwright-temporal-web-client-tls", "tls.key"),
        @("tenantwright-temporal-web-oidc", "client-id"),
        @("tenantwright-temporal-web-oidc", "client-secret"),
        @("tenantwright-temporal-namespace-bootstrap", "api-key"),
        @("tenantwright-temporal-namespace-bootstrap-tls", "ca.crt"),
        @("tenantwright-temporal-namespace-bootstrap-tls", "tls.crt"),
        @("tenantwright-temporal-namespace-bootstrap-tls", "tls.key")
    )) {
        if ($rendered -match [regex]::Escape($requirement[0])) {
            Assert-SecretKey -SecretName $requirement[0] -Key $requirement[1]
        }
    }

    $upgradeArguments = @(
        "upgrade", "--install", $ReleaseName, $chartPath,
        "--namespace", $Namespace,
        "--rollback-on-failure", "--wait", "--history-max", "10", "--timeout", "15m"
    ) + $valueArguments
    Invoke-CheckedCommand -Command $HelmPath -Arguments $upgradeArguments `
        -FailureMessage "Temporal Helm install/upgrade failed and was rolled back."
}
finally {
    Remove-Item -LiteralPath $renderPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Temporal release '$ReleaseName' is installed in namespace '$Namespace'." -ForegroundColor Green
