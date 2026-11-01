#!/usr/bin/env pwsh
# Synchronize the canonical .agents/skills catalog into tool-specific mirrors
# and keep .agents/manifest.json deterministic. Edit only .agents/skills, then
# run this script. Use -Check in verification and CI-style workflows.

param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot '.agents/skills'
$manifestPath = Join-Path $repoRoot '.agents/manifest.json'
$mirrors = @(
    @{ Path = (Join-Path $repoRoot '.kiro/skills'); Exclude = @() },
    # Project skills override bundled skills with the same name, so both tool
    # mirrors receive the complete canonical TenantWright package set.
    @{ Path = (Join-Path $repoRoot '.claude/skills'); Exclude = @() }
)

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Canonical skills directory not found: $source"
}

function Get-CanonicalSkillDirectories {
    @(Get-ChildItem -LiteralPath $source -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') -PathType Leaf } |
        Sort-Object Name)
}

function Get-ManifestText([System.IO.DirectoryInfo[]]$skills) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('{')
    $lines.Add('  "skills": {')

    for ($index = 0; $index -lt $skills.Count; $index++) {
        $skillName = $skills[$index].Name
        $separator = if ($index -lt $skills.Count - 1) { ',' } else { '' }
        $lines.Add("    `"$skillName`": {")
        $lines.Add('      "status": "installed",')
        $lines.Add('      "disabled": false,')
        $lines.Add('      "error": null')
        $lines.Add("    }$separator")
    }

    $lines.Add('  }')
    $lines.Add('}')
    ($lines -join "`n") + "`n"
}

function Get-TreeHashes(
    [string]$root,
    [string[]]$exclude
) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return @()
    }

    $base = (Resolve-Path -LiteralPath $root).Path
    @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($base.Length)
        $segments = $relative -split '[\\/]'
        $skillName = if ($segments.Length -gt 1) { $segments[1] } else { '' }
        if ($exclude -notcontains $skillName) {
            "$relative`:$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
        }
    } | Sort-Object)
}

$skills = Get-CanonicalSkillDirectories
if ($skills.Count -eq 0) {
    throw 'No canonical skills containing SKILL.md were found.'
}

$expectedManifest = Get-ManifestText $skills

if ($Check) {
    $outOfSync = $false

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Write-Warning "Manifest missing: $manifestPath"
        $outOfSync = $true
    }
    else {
        $actualManifest = (Get-Content -LiteralPath $manifestPath -Raw) -replace "`r`n", "`n"
        if ($actualManifest -ne $expectedManifest) {
            Write-Warning "OUT OF SYNC: $manifestPath"
            $outOfSync = $true
        }
    }

    foreach ($mirror in $mirrors) {
        if (-not (Test-Path -LiteralPath $mirror.Path -PathType Container)) {
            Write-Warning "Mirror missing: $($mirror.Path)"
            $outOfSync = $true
            continue
        }

        $sourceHashes = Get-TreeHashes $source $mirror.Exclude
        $mirrorHashes = Get-TreeHashes $mirror.Path @()
        $diff = Compare-Object $sourceHashes $mirrorHashes
        if ($diff) {
            Write-Warning "OUT OF SYNC: $($mirror.Path)"
            $diff | Format-Table -AutoSize
            $outOfSync = $true
        }
    }

    if ($outOfSync) {
        throw 'Skill manifest or mirrors are out of sync. Run scripts/sync-skills.ps1.'
    }

    Write-Host 'Skill manifest and mirrors are in sync.' -ForegroundColor Green
    exit 0
}

[System.IO.File]::WriteAllText(
    $manifestPath,
    $expectedManifest,
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $manifestPath" -ForegroundColor Green

foreach ($mirror in $mirrors) {
    if (Test-Path -LiteralPath $mirror.Path) {
        Remove-Item -LiteralPath $mirror.Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $mirror.Path | Out-Null

    foreach ($skill in $skills) {
        if ($mirror.Exclude -contains $skill.Name) {
            continue
        }
        Copy-Item -LiteralPath $skill.FullName -Destination (Join-Path $mirror.Path $skill.Name) -Recurse -Force
    }

    $excluded = if ($mirror.Exclude.Count -eq 0) { 'none' } else { $mirror.Exclude -join ', ' }
    Write-Host "Synced .agents/skills -> $($mirror.Path) (excluded: $excluded)" -ForegroundColor Green
}
