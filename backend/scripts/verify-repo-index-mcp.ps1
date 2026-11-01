param()

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$mcpProject = Join-Path $repoRoot "tools\RepoIndexMcp\RepoIndexMcp.csproj"
$testProject = Join-Path $repoRoot "tests\RepoIndexMcp.Tests\RepoIndexMcp.Tests.csproj"
$artifactsRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tenantwright-repo-index-mcp-verify-" + [System.Guid]::NewGuid().ToString("N"))
$dotnetArgs = @("--nologo", "--artifacts-path", $artifactsRoot)

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

try {
    Write-Host "Building RepoIndex MCP in isolated artifacts..."
    dotnet build $mcpProject @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "RepoIndex MCP build failed."
    }

    Write-Host "Running RepoIndex MCP tests in isolated artifacts..."
    # The isolated build restored the MCP graph, not this separate test project's SDK imports.
    # Omitting its restore can make VSTest silently skip execution and return a false success.
    dotnet test $testProject @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "RepoIndex MCP tests failed."
    }
}
finally {
    if (Test-Path -LiteralPath $artifactsRoot) {
        $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactsRoot)
        $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedArtifacts.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolvedArtifacts) -notmatch '^tenantwright-repo-index-mcp-verify-[0-9a-f]{32}$') {
            throw "Refusing cleanup outside the owned temporary verification directory."
        }
        Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
    }
}
