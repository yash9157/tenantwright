using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace RepoIndexMcp.Tests;

public sealed class RepoIndexVerificationScriptTests
{
    private const string FirstCommit = "1111111111111111111111111111111111111111";
    private const string SecondCommit = "2222222222222222222222222222222222222222";

    [Theory]
    [InlineData(SecondCommit)]
    [InlineData("abcdefABCDEFabcdefABCDEFabcdefABCDEFabcd")]
    [InlineData("UNKNOWN")]
    public async Task Root_manifest_ignores_only_valid_generation_provenance(string generatedCommit)
    {
        var expected = Manifest(FirstCommit);
        var generated = Manifest(generatedCommit);
        generated["generatedAtUtc"] = "2026-10-12T10:00:00.123456Z";

        var result = await NormalizeAsync(expected, generated, "manifest.json", ignoreGenerationCommit: true);

        Assert.Null(result.Error);
        Assert.Equal(result.Expected, result.Generated);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("solutionPath")]
    [InlineData("content")]
    [InlineData("path")]
    public async Task Semantic_manifest_properties_are_not_hidden_by_provenance_normalization(string property)
    {
        var expected = Manifest(FirstCommit);
        var generated = Manifest(SecondCommit);
        generated[property] = "changed-semantic-value";

        var result = await NormalizeAsync(expected, generated, "manifest.json", ignoreGenerationCommit: true);

        Assert.Null(result.Error);
        Assert.NotEqual(result.Expected, result.Generated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("unknown")]
    [InlineData("111111111111111111111111111111111111111")]
    [InlineData("11111111111111111111111111111111111111111")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public async Task Missing_or_invalid_manifest_commit_is_rejected_instead_of_ignored(string? commit)
    {
        var generated = Manifest(SecondCommit);
        if (commit is null) generated.Remove("gitCommit");
        else generated["gitCommit"] = commit;

        var result = await NormalizeAsync(Manifest(FirstCommit), generated, "manifest.json", ignoreGenerationCommit: true);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Null_or_non_string_manifest_commit_is_rejected()
    {
        foreach (JsonNode? commit in new JsonNode?[] { null, JsonValue.Create(123), new JsonArray(FirstCommit) })
        {
            var generated = Manifest(SecondCommit);
            generated["gitCommit"] = commit;
            var result = await NormalizeAsync(Manifest(FirstCommit), generated, "manifest.json", ignoreGenerationCommit: true);
            Assert.NotNull(result.Error);
        }
    }

    [Theory]
    [InlineData("routes.json")]
    [InlineData("manifest.json")]
    public async Task Filename_alone_does_not_suppress_commit_content_without_explicit_root_manifest_switch(string fileName)
    {
        var result = await NormalizeAsync(Manifest(FirstCommit), Manifest(SecondCommit), fileName, ignoreGenerationCommit: false);

        Assert.Null(result.Error);
        Assert.NotEqual(result.Expected, result.Generated);
    }

    [Fact]
    public async Task Nested_manifest_generation_commit_remains_semantic_content()
    {
        var expected = Manifest(FirstCommit);
        var generated = Manifest(SecondCommit);
        expected["nested"] = new JsonObject { ["gitCommit"] = FirstCommit };
        generated["nested"] = new JsonObject { ["gitCommit"] = SecondCommit };

        var result = await NormalizeAsync(expected, generated, "manifest.json", ignoreGenerationCommit: true);

        Assert.Null(result.Error);
        Assert.NotEqual(result.Expected, result.Generated);
    }

    [Fact]
    public async Task Nested_manifest_filename_is_not_a_generation_provenance_exception()
    {
        var result = await NormalizeAsync(Manifest(FirstCommit), Manifest(SecondCommit),
            Path.Combine("nested", "manifest.json"), ignoreGenerationCommit: false);

        Assert.Null(result.Error);
        Assert.NotEqual(result.Expected, result.Generated);
    }

    private static JsonObject Manifest(string commit) => new()
    {
        ["schemaVersion"] = "1.2.0",
        ["generatedAtUtc"] = "2026-10-11T12:00:00.123456Z",
        ["gitCommit"] = commit,
        ["solutionPath"] = "TenantWright.slnx",
        ["path"] = "index/services/Synthetic/routes.json",
        ["content"] = "synthetic-index-content"
    };

    private static async Task<NormalizationResult> NormalizeAsync(JsonObject expected, JsonObject generated,
        string fileName, bool ignoreGenerationCommit, [CallerFilePath] string sourceFilePath = "")
    {
        var scriptPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!,
            "..", "..", "scripts", "generate-repo-index.ps1"));
        Assert.True(File.Exists(scriptPath), "The actual repository index script must be available.");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "tenantwright-index-normalization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var expectedDirectory = Directory.CreateDirectory(Path.Combine(temporaryRoot, "expected")).FullName;
            var generatedDirectory = Directory.CreateDirectory(Path.Combine(temporaryRoot, "generated")).FullName;
            var expectedPath = Path.Combine(expectedDirectory, fileName);
            var generatedPath = Path.Combine(generatedDirectory, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
            await File.WriteAllTextAsync(expectedPath, expected.ToJsonString());
            await File.WriteAllTextAsync(generatedPath, generated.ToJsonString());

            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = temporaryRoot
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(NormalizationHarness)) })
                start.ArgumentList.Add(argument);
            start.Environment["TENANTWRIGHT_INDEX_TEST_SCRIPT"] = scriptPath;
            start.Environment["TENANTWRIGHT_INDEX_TEST_EXPECTED"] = expectedPath;
            start.Environment["TENANTWRIGHT_INDEX_TEST_GENERATED"] = generatedPath;
            start.Environment["TENANTWRIGHT_INDEX_TEST_IGNORE_COMMIT"] = ignoreGenerationCommit ? "true" : "false";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the PowerShell script-function test.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("The bounded index-normalization function test timed out.");
            }
            Assert.True(process.ExitCode == 0, await stderr);
            return JsonSerializer.Deserialize<NormalizationResult>(await stdout)
                ?? throw new InvalidOperationException("The PowerShell normalization harness returned no result.");
        }
        finally
        {
            // This exact UUID-owned directory contains only this invocation's two synthetic JSON files.
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private sealed record NormalizationResult(string? Expected, string? Generated, string? Error);

    private const string NormalizationHarness = """
        $ErrorActionPreference = 'Stop'
        $taskTokens = $null
        $taskErrors = $null
        $taskAst = [System.Management.Automation.Language.Parser]::ParseFile(
            $env:TENANTWRIGHT_INDEX_TEST_SCRIPT, [ref]$taskTokens, [ref]$taskErrors)
        if ($taskErrors.Count -ne 0) { throw 'The actual index script contains PowerShell parse errors.' }
        $taskFunctions = @($taskAst.FindAll({ param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Normalize-JsonFile'
        }, $true))
        if ($taskFunctions.Count -ne 1) { throw 'Exactly one actual Normalize-JsonFile definition is required.' }
        # Evaluate only the extracted function definition, never the generator or its verification loop.
        Invoke-Expression $taskFunctions[0].Extent.Text
        $taskArguments = @{}
        if ($env:TENANTWRIGHT_INDEX_TEST_IGNORE_COMMIT -eq 'true') { $taskArguments.IgnoreGenerationCommit = $true }
        try {
            $taskExpected = Normalize-JsonFile -Path $env:TENANTWRIGHT_INDEX_TEST_EXPECTED @taskArguments
            $taskGenerated = Normalize-JsonFile -Path $env:TENANTWRIGHT_INDEX_TEST_GENERATED @taskArguments
            @{ Expected = $taskExpected; Generated = $taskGenerated; Error = $null } | ConvertTo-Json -Compress
        }
        catch {
            @{ Expected = $null; Generated = $null; Error = $_.Exception.Message } | ConvertTo-Json -Compress
        }
        """;
}
