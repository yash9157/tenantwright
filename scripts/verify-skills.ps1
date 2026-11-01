#!/usr/bin/env pwsh
# Validate canonical skill package structure, metadata, links, and mirrors.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$skillsRoot = Join-Path $repoRoot '.agents/skills'
$errors = [System.Collections.Generic.List[string]]::new()

function Add-ValidationError([string]$message) {
    $errors.Add($message)
}

function Get-Frontmatter([string]$path) {
    $lines = Get-Content -LiteralPath $path
    if ($lines.Count -lt 4 -or $lines[0] -ne '---') {
        Add-ValidationError "$path has no opening YAML frontmatter delimiter."
        return @{}
    }

    $end = [Array]::IndexOf($lines, '---', 1)
    if ($end -lt 2) {
        Add-ValidationError "$path has no closing YAML frontmatter delimiter."
        return @{}
    }

    $values = @{}
    foreach ($line in $lines[1..($end - 1)]) {
        if ($line -match '^([a-z_]+):\s*(.+)$') {
            $values[$Matches[1]] = $Matches[2].Trim().Trim('"', "'")
        }
    }
    $values
}

function Test-MarkdownLinks([System.IO.FileInfo]$markdownFile) {
    $text = Get-Content -LiteralPath $markdownFile.FullName -Raw
    foreach ($match in [regex]::Matches($text, '\[[^\]]+\]\(([^)#]+)(?:#[^)]+)?\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^(https?://|mailto:|/)' -or $target -match '[<>{}]') {
            continue
        }

        $resolved = Join-Path $markdownFile.DirectoryName $target
        if (-not (Test-Path -LiteralPath $resolved)) {
            Add-ValidationError "$($markdownFile.FullName) references missing path '$target'."
        }
    }
}

function Test-SkillStructure(
    [System.IO.DirectoryInfo]$skill,
    [System.IO.FileInfo]$skillFile
) {
    $references = Join-Path $skill.FullName 'references'
    if (-not (Test-Path -LiteralPath $references -PathType Container)) {
        Add-ValidationError "$($skill.Name) has no references directory for repository-specific detail."
        return
    }

    $nested = @(Get-ChildItem -LiteralPath $references -Directory -Recurse)
    if ($nested.Count -gt 0) {
        Add-ValidationError "$($skill.Name) has nested reference directories; keep references one level deep."
    }

    $skillText = Get-Content -LiteralPath $skillFile.FullName -Raw
    foreach ($reference in Get-ChildItem -LiteralPath $references -File) {
        $directPath = "references/$($reference.Name)"
        if ($skillText.IndexOf($directPath, [StringComparison]::Ordinal) -lt 0) {
            Add-ValidationError "$($skill.Name)/SKILL.md does not directly link '$directPath'."
        }
    }

    $readme = @(Get-ChildItem -LiteralPath $skill.FullName -Recurse -File |
        Where-Object { $_.Name -ieq 'README.md' })
    if ($readme.Count -gt 0) {
        Add-ValidationError "$($skill.Name) contains README.md; keep skill instructions in SKILL.md/references."
    }
}

function Test-AgentMetadata([System.IO.DirectoryInfo]$skill) {
    $metadataPath = Join-Path $skill.FullName 'agents/openai.yaml'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        Add-ValidationError "$($skill.Name) is missing agents/openai.yaml."
        return
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw
    if ($metadata -notmatch '(?m)^\s*display_name:\s*".+"\s*$') {
        Add-ValidationError "$($skill.Name) metadata has no quoted display_name."
    }
    if ($metadata -notmatch '(?m)^\s*short_description:\s*"([^"]+)"\s*$') {
        Add-ValidationError "$($skill.Name) metadata has no quoted short_description."
    }
    else {
        $length = $Matches[1].Length
        if ($length -lt 25 -or $length -gt 64) {
            Add-ValidationError "$($skill.Name) short_description length is $length; expected 25-64."
        }
    }

    $escapedName = [regex]::Escape("`$$($skill.Name)")
    if ($metadata -notmatch "(?m)^\s*default_prompt:\s*`"[^`"]*$escapedName[^`"]*`"\s*$") {
        Add-ValidationError "$($skill.Name) default_prompt must name `$$($skill.Name)."
    }
}

if (-not (Test-Path -LiteralPath $skillsRoot -PathType Container)) {
    throw "Canonical skills directory not found: $skillsRoot"
}

$skillDirectories = @(
    Get-ChildItem -LiteralPath $skillsRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') -PathType Leaf } |
        Sort-Object Name
)
$skillNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

foreach ($skill in $skillDirectories) {
    $skillFile = Get-Item -LiteralPath (Join-Path $skill.FullName 'SKILL.md')
    $frontmatter = Get-Frontmatter $skillFile.FullName

    if (-not $skillNames.Add($skill.Name)) {
        Add-ValidationError "Duplicate skill directory '$($skill.Name)'."
    }
    if ($frontmatter['name'] -ne $skill.Name) {
        Add-ValidationError "$($skill.Name) frontmatter name '$($frontmatter['name'])' does not match its directory."
    }
    if ([string]::IsNullOrWhiteSpace($frontmatter['description'])) {
        Add-ValidationError "$($skill.Name) has no trigger description."
    }

    $lineCount = (Get-Content -LiteralPath $skillFile.FullName).Count
    if ($lineCount -gt 500) {
        Add-ValidationError "$($skill.Name)/SKILL.md has $lineCount lines; maximum is 500."
    }

    Test-MarkdownLinks $skillFile
    Test-SkillStructure $skill $skillFile
    Test-AgentMetadata $skill
}

try {
    & (Join-Path $repoRoot 'scripts/sync-skills.ps1') -Check
    if ($LASTEXITCODE -ne 0) {
        Add-ValidationError 'scripts/sync-skills.ps1 -Check failed.'
    }
}
catch {
    Add-ValidationError "Skill synchronization check failed: $($_.Exception.Message)"
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    throw "Skill verification failed with $($errors.Count) error(s)."
}

Write-Host "Verified $($skillDirectories.Count) canonical skill packages: frontmatter, direct reference links, agent metadata, manifest, and mirrors." -ForegroundColor Green
