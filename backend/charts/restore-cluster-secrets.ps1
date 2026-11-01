<#
.SYNOPSIS
    Restores Kubernetes Secrets from a cluster-secrets-backup.local.json export.

.DESCRIPTION
    Companion to a planned namespace Secret wipe. The backup is produced by
    exporting every non-Helm Secret from the app namespaces; this script applies
    them back.

    Helm release history (sh.helm.release.v1.*) is deliberately NOT part of the
    backup and therefore never restored. Deleting Helm release Secrets destroys
    Helm's record of every release in the namespace, so they must be preserved
    in place rather than round-tripped.

    Secrets whose contents are derived from a source of truth elsewhere can be
    excluded with -SkipDatabaseSecrets (per-service `-db` connection strings,
    which come from app-database-secrets.local.yaml) and
    -SkipPostgresqlCaSecrets (per-service `-postgresql-ca` bundles, which must
    be regenerated from the instance PEM that matches the CURRENT allocation --
    restoring a stale CA points a service at the wrong instance root).

.EXAMPLE
    ./restore-cluster-secrets.ps1 -DryRun

.EXAMPLE
    ./restore-cluster-secrets.ps1 -SkipDatabaseSecrets -SkipPostgresqlCaSecrets
#>
[CmdletBinding()]
param(
    [ValidateSet("dev", "production")][string]$Environment = "dev",
    [string]$BackupFile = "",
    [string]$Context = "",
    [string[]]$Namespace = @(),
    [switch]$SkipDatabaseSecrets,
    [switch]$SkipPostgresqlCaSecrets,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# $PSScriptRoot is not reliably populated while param() defaults are evaluated,
# so resolve the default backup location here instead.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($BackupFile)) {
    $BackupFile = Join-Path $scriptRoot "environments/$Environment/private/cluster-secrets-backup.local.json"
}

function Assert-CommandExists {
    param([Parameter(Mandatory = $true)][string]$CommandName)
    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $CommandName"
    }
}

Assert-CommandExists -CommandName "kubectl"

if (-not (Test-Path -LiteralPath $BackupFile)) {
    throw "Backup file not found: $BackupFile"
}

$backup = Get-Content -LiteralPath $BackupFile -Raw | ConvertFrom-Json

if ($null -eq $backup.namespaces) {
    throw "Backup file '$BackupFile' has no 'namespaces' section. It was not produced by the expected export."
}

Write-Host "==> backup taken at $($backup.exportedAtUtc) from context '$($backup.context)'" -ForegroundColor Cyan

$contextArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Context)) {
    $contextArgs = @("--context", $Context)
}

$restored = 0
$skipped = 0

foreach ($nsProperty in $backup.namespaces.PSObject.Properties) {
    $ns = $nsProperty.Name

    if ($Namespace.Count -gt 0 -and $Namespace -notcontains $ns) {
        Write-Host "==> skipping namespace '$ns' (not selected)" -ForegroundColor DarkGray
        continue
    }

    $items = @($nsProperty.Value)
    $selected = @()

    foreach ($item in $items) {
        $name = [string]$item.metadata.name

        # Never restore Helm release history; it is not backed up and must stay in place.
        if ($name -like "sh.helm.release.v1.*") {
            $skipped++
            continue
        }
        if ($SkipDatabaseSecrets -and ($name -like "*-db" -or $name -like "*-db-admin")) {
            Write-Host "    skip (database, sourced from secrets file): $ns/$name" -ForegroundColor DarkGray
            $skipped++
            continue
        }
        if ($SkipPostgresqlCaSecrets -and $name -like "*-postgresql-ca") {
            Write-Host "    skip (CA, regenerate from instance PEM): $ns/$name" -ForegroundColor DarkGray
            $skipped++
            continue
        }

        $selected += $item
    }

    if ($selected.Count -eq 0) {
        Write-Host "==> namespace '$ns': nothing to restore" -ForegroundColor DarkGray
        continue
    }

    $payload = [ordered]@{
        apiVersion = "v1"
        kind       = "List"
        items      = $selected
    }

    $tempFile = Join-Path ([System.IO.Path]::GetTempPath()) ("restore-$ns-" + [guid]::NewGuid().ToString("N") + ".json")
    try {
        $payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $tempFile -Encoding UTF8

        $applyArgs = $contextArgs + @("-n", $ns, "apply", "-f", $tempFile)
        if ($DryRun) {
            $applyArgs += "--dry-run=client"
        }

        Write-Host "==> kubectl $($applyArgs -join ' ')" -ForegroundColor Cyan
        & kubectl @applyArgs
        if ($LASTEXITCODE -ne 0) {
            throw "kubectl apply failed for namespace '$ns'."
        }

        $restored += $selected.Count
    }
    finally {
        if (Test-Path -LiteralPath $tempFile) {
            Remove-Item -LiteralPath $tempFile -Force
        }
    }
}

Write-Host ""
if ($DryRun) {
    Write-Host "Dry run complete. Validated $restored Secret(s), skipped $skipped." -ForegroundColor Yellow
}
else {
    Write-Host "Restored $restored Secret(s), skipped $skipped." -ForegroundColor Green
}

if ($SkipPostgresqlCaSecrets) {
    Write-Host "Reminder: regenerate each tenantwright-<service>-postgresql-ca from the matching instance PEM in environments/$Environment/private/postgresql-ca/." -ForegroundColor Yellow
}
if ($SkipDatabaseSecrets) {
    Write-Host "Reminder: apply database secrets from environments/$Environment/private/app-secrets.local.yaml via apply-secrets.ps1 -Environment $Environment." -ForegroundColor Yellow
}
