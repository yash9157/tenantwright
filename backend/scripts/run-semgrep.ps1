[CmdletBinding()]
param(
    [ValidateSet("INFO", "WARNING", "ERROR")]
    [string]$MinimumSeverity = "WARNING",
    [int]$MaxFindingsToPrint = 10,
    [switch]$AllowFindings
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$tempOutputPath = Join-Path ([System.IO.Path]::GetTempPath()) ("tenantwright-semgrep-" + [System.Guid]::NewGuid().ToString("N") + ".json")
$configs = @(
    "p/csharp",
    "p/security-audit",
    "p/secrets"
)

function Add-PreferredToolPaths {
    if (Get-Command python -ErrorAction SilentlyContinue) {
        $pythonScripts = ((@'
import os
import sys
import site

print(os.path.join(site.getuserbase(), f"Python{sys.version_info.major}{sys.version_info.minor}", "Scripts"))
'@ | python -) | Select-Object -First 1).Trim()

        if (-not [string]::IsNullOrWhiteSpace($pythonScripts) -and (Test-Path -LiteralPath $pythonScripts)) {
            $pathEntries = $env:PATH -split [regex]::Escape([System.IO.Path]::PathSeparator)
            if ($pathEntries -notcontains $pythonScripts) {
                $env:PATH = "$pythonScripts$([System.IO.Path]::PathSeparator)$env:PATH"
            }
        }
    }
}

Add-PreferredToolPaths

if (-not (Get-Command semgrep -ErrorAction SilentlyContinue)) {
    throw "Semgrep is required. Install it (for example 'python -m pip install --user semgrep') and ensure it is on PATH."
}

$env:SEMGREP_SEND_METRICS = "off"

$semgrepArgs = @(
    "scan",
    "--json",
    "--output", $tempOutputPath,
    "--severity", $MinimumSeverity,
    "--error",
    "--exclude", ".gitnexus",
    "--exclude", "graphify-out",
    "--exclude", "output/graphify",
    "--exclude", "index",
    "--exclude", "bin",
    "--exclude", "obj"
)

foreach ($config in $configs) {
    $semgrepArgs += @("--config", $config)
}

$semgrepArgs += "."

Push-Location $repoRoot
try {
    & semgrep @semgrepArgs
    $scanExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $tempOutputPath)) {
    throw "Semgrep did not produce JSON output. Exit code: $scanExitCode"
}

try {
    $payload = Get-Content -LiteralPath $tempOutputPath -Raw | ConvertFrom-Json
}
finally {
    Remove-Item -LiteralPath $tempOutputPath -Force -ErrorAction SilentlyContinue
}

$results = @($payload.results)

if ($results.Count -eq 0) {
    Write-Host "Semgrep completed with no findings at severity >= $MinimumSeverity."
    Write-Host "Profiles: $($configs -join ", ")"
    return
}

$severityCounts = $results |
    Group-Object { $_.extra.severity } |
    Sort-Object Name

Write-Host "Semgrep findings summary:"
foreach ($group in $severityCounts) {
    Write-Host ("- {0}: {1}" -f $group.Name, $group.Count)
}

Write-Host ""
Write-Host "First $([Math]::Min($results.Count, $MaxFindingsToPrint)) findings:"

$results |
    Select-Object -First $MaxFindingsToPrint |
    ForEach-Object {
        $line = if ($_.start.line) { $_.start.line } else { 1 }
        $severity = if ($_.extra.severity) { $_.extra.severity } else { "UNKNOWN" }
        $message = if ($_.extra.message) { $_.extra.message } else { "No message provided." }
        Write-Host ("- [{0}] {1}:{2} {3} ({4})" -f $severity, $_.path, $line, $message, $_.check_id)
    }

if ($scanExitCode -eq 1) {
    if ($AllowFindings) {
        Write-Host "Semgrep reported findings, but findings are allowed for this verification run."
        return
    }

    throw "Semgrep reported findings. Review the summary above."
}

if ($scanExitCode -ne 0) {
    throw "Semgrep scan failed with exit code $scanExitCode."
}
