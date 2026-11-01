[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Convert-ToRepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return ($Path -replace "\\", "/")
}

function Test-IsAllowedEtoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ($Path -match "^services/[^/]+/[^/]+\.Domain\.Shared/") {
        return $true
    }

    if ($Path -match "^shared/Shared\.(EventContracts|EventBus|AuditLogs)/") {
        return $true
    }

    if ($Path -match "^shared/Shared\.Domain\.Shared/Entities/Events/Distributed/") {
        return $true
    }

    if ($Path -match "^tests/") {
        return $true
    }

    return $false
}

function Get-ExpectedLocationClass {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ($Path -match "^services/[^/]+/[^/]+\.Application\.Contracts/Events/") {
        return "Service-owned distributed ETOs must live in the owning service's *.Domain.Shared project"
    }

    if ($Path -match "^services/[^/]+/") {
        return "Service-owned distributed ETOs must live in the owning service's *.Domain.Shared project"
    }

    if ($Path -match "^shared/Shared\.(EventBus|AuditLogs)/") {
        return "Platform ETOs are allowed only in Shared.EventContracts, Shared.EventBus, Shared.AuditLogs, or the core distributed entity-event owner"
    }

    return "Distributed ETOs must follow ADR-008 service or platform ownership"
}

$trackedFiles = @(& git ls-files -- "*.cs")
if ($LASTEXITCODE -ne 0) {
    throw "Failed to enumerate tracked C# files with git ls-files."
}

$declarationPattern = [regex]'(?m)^\s*(?:public|internal|protected|private)?\s*(?:sealed|abstract|partial|static)?\s*(?:class|interface|record(?:\s+class|\s+struct)?)\s+([A-Za-z_][A-Za-z0-9_]*Eto)\b'
$violations = New-Object System.Collections.Generic.List[object]

foreach ($file in $trackedFiles) {
    $normalizedPath = Convert-ToRepoPath -Path $file
    if ($normalizedPath -match "(^|/)(obj|bin|artifacts/tmpbuild)(/|$)") {
        continue
    }

    if (-not (Test-Path $file)) {
        continue
    }

    $content = Get-Content -Path $file -Raw
    $matches = $declarationPattern.Matches($content)
    if ($matches.Count -eq 0) {
        continue
    }

    foreach ($match in $matches) {
        $typeName = $match.Groups[1].Value
        if (Test-IsAllowedEtoPath -Path $normalizedPath) {
            continue
        }

        $violations.Add([pscustomobject]@{
                File = $normalizedPath
                Type = $typeName
                Expected = Get-ExpectedLocationClass -Path $normalizedPath
            }) | Out-Null
    }
}

if ($violations.Count -eq 0) {
    Write-Host ("ETO placement verification passed. Checked {0} tracked C# files." -f $trackedFiles.Count) -ForegroundColor Green
    exit 0
}

Write-Host "ETO placement verification failed." -ForegroundColor Red
Write-Host ""
foreach ($violation in $violations | Sort-Object File, Type) {
    Write-Host ("- File: {0}" -f $violation.File) -ForegroundColor Yellow
    Write-Host ("  Type: {0}" -f $violation.Type) -ForegroundColor Yellow
    Write-Host ("  Expected: {0}" -f $violation.Expected) -ForegroundColor Yellow
    Write-Host ""
}

exit 1
