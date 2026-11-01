[CmdletBinding()]
param(
    [ValidateSet("dev", "production")][string]$Environment = "dev",
    [Parameter(Mandatory = $true)]
    [string]$Username,

    [string]$Namespace = "tenantwright-telemetry",
    [string]$SecretName = "tenantwright-opensearch-credentials",
    [string]$LocalSecretPath = "",
    [switch]$PasswordFromClipboard,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

if (-not $PasswordFromClipboard) {
    throw "Supply -PasswordFromClipboard. Password command-line parameters are intentionally unsupported."
}

if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    throw "Required command not found: kubectl"
}

if ([string]::IsNullOrWhiteSpace($LocalSecretPath)) {
    $LocalSecretPath = Join-Path $PSScriptRoot "environments/$Environment/private/tenantwright-opensearch-credentials.local.yaml"
}

$password = Get-Clipboard -Raw
if ([string]::IsNullOrWhiteSpace($password) -or $password -match "[`r`n]") {
    throw "The clipboard must contain exactly one non-empty password value."
}

try {
    $usernameBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Username))
    $passwordBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($password))
    $manifest = @"
apiVersion: v1
kind: Secret
metadata:
  name: $SecretName
  namespace: $Namespace
type: Opaque
data:
  username: $usernameBase64
  password: $passwordBase64
"@

    $resolvedPath = [IO.Path]::GetFullPath($LocalSecretPath)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "environments/$Environment/private"))
    if (-not $resolvedPath.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "LocalSecretPath must stay under '$allowedRoot'."
    }

    [IO.Directory]::CreateDirectory((Split-Path -Parent $resolvedPath)) | Out-Null
    [IO.File]::WriteAllText($resolvedPath, $manifest, [Text.UTF8Encoding]::new($false))
    Write-Host "Updated ignored operator Secret manifest: $resolvedPath" -ForegroundColor Green

    if ($Apply) {
        & kubectl apply -f $resolvedPath
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to apply Secret '$Namespace/$SecretName'."
        }
        Write-Host "Applied Secret '$Namespace/$SecretName'." -ForegroundColor Green
    }
    else {
        Write-Host "No cluster change was made. Pass -Apply after reviewing the target context." -ForegroundColor Yellow
    }
}
finally {
    $password = $null
    $passwordBase64 = $null
    $manifest = $null
}
