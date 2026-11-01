param(
    [switch]$Verify
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repoRoot "TenantWright.slnx"
$indexPath = Join-Path $repoRoot "index"
$projectPath = Join-Path $repoRoot "tools\RepoIndexer\RepoIndexer.csproj"
function Normalize-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$IgnoreGenerationCommit
    )

    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($json.PSObject.Properties.Name -contains "generatedAtUtc") {
        $json.generatedAtUtc = "<ignored>"
    }
    # This is the HEAD observed while generating, not index content. Committing the
    # generated files necessarily creates another HEAD; it cannot hash itself.
    if ($IgnoreGenerationCommit) {
        if ($json.PSObject.Properties.Name -notcontains "gitCommit" -or
            $json.gitCommit -isnot [string] -or
            $json.gitCommit -cnotmatch '^(?:[0-9a-fA-F]{40}|UNKNOWN)$') {
            throw "Index manifest requires a valid generation commit or UNKNOWN provenance."
        }
        $json.gitCommit = "<generation-head>"
    }

    return ($json | ConvertTo-Json -Depth 100 -Compress)
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath)
    if (-not $baseFullPath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $baseFullPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $baseUri = New-Object System.Uri($baseFullPath)
    $targetUri = New-Object System.Uri([System.IO.Path]::GetFullPath($TargetPath))
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

if ($Verify) {
    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("tenantwright-repo-index-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempPath | Out-Null

    try {
        dotnet run --project $projectPath -- --repo-root $repoRoot --solution $solutionPath --output $tempPath

        $expectedFiles = Get-ChildItem -LiteralPath $indexPath -Recurse -File | ForEach-Object {
            Get-RelativePath -BasePath $indexPath -TargetPath $_.FullName
        } | Sort-Object

        $generatedFiles = Get-ChildItem -LiteralPath $tempPath -Recurse -File | ForEach-Object {
            Get-RelativePath -BasePath $tempPath -TargetPath $_.FullName
        } | Sort-Object

        if ((@($expectedFiles) -join "`n") -ne (@($generatedFiles) -join "`n")) {
            throw "Index file set drift detected. Run scripts/generate-repo-index.ps1 and commit the updated index."
        }

        foreach ($relativePath in $expectedFiles) {
            $expectedPath = Join-Path $indexPath $relativePath
            $generatedPath = Join-Path $tempPath $relativePath

            $isRootManifest = $relativePath -eq "manifest.json"
            if ((Normalize-JsonFile -Path $expectedPath -IgnoreGenerationCommit:$isRootManifest) -ne
                (Normalize-JsonFile -Path $generatedPath -IgnoreGenerationCommit:$isRootManifest)) {
                throw "Index drift detected in '$relativePath'. Run scripts/generate-repo-index.ps1 and commit the updated index."
            }
        }

        Write-Host "Repo index is up to date."
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Recurse -Force
        }
    }

    return
}

dotnet run --project $projectPath -- --repo-root $repoRoot --solution $solutionPath --output $indexPath
Write-Host "Repo index generated at $indexPath"
