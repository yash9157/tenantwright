using System.Runtime.CompilerServices;
using System.Text.Json;
using TenantWright.Tools.RepoIndex;
using Xunit;

namespace RepoIndexMcp.Tests;

public sealed class RepoIndexQueryServiceTests : IClassFixture<RepoIndexFixture>
{
    private readonly RepoIndexFixture _fixture;

    public RepoIndexQueryServiceTests(RepoIndexFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Generated_overviews_include_identity_auth_and_gateway()
    {
        var manifest = _fixture.ReadJson<List<ScopeOverviewManifestEntry>>("overviews.json");

        Assert.Contains(manifest, entry => entry.Scope == "Identity");
        Assert.Contains(manifest, entry => entry.Scope == "Auth");
        Assert.Contains(manifest, entry => entry.Scope == "ApiGateway");
        Assert.True(File.Exists(Path.Combine(_fixture.IndexPath, "services", "Identity", "overview.json")));
        Assert.True(File.Exists(Path.Combine(_fixture.IndexPath, "services", "Auth", "overview.json")));
        Assert.True(File.Exists(Path.Combine(_fixture.IndexPath, "services", "ApiGateway", "overview.json")));
    }

    [Fact]
    public void Scope_local_persistence_and_dependencies_files_are_generated()
    {
        Assert.True(File.Exists(Path.Combine(_fixture.IndexPath, "services", "Identity", "persistence.json")));
        Assert.True(File.Exists(Path.Combine(_fixture.IndexPath, "services", "Identity", "dependencies.json")));
        Assert.True(File.Exists(Path.Combine(_fixture.IndexPath, "services", "ApiGateway", "persistence.json")));
        Assert.True(File.Exists(Path.Combine(_fixture.IndexPath, "services", "ApiGateway", "dependencies.json")));
    }

    [Fact]
    public void Permissions_persistence_discovers_registered_entity_type_catalog()
    {
        var persistence = _fixture.ReadJson<List<PersistenceIndexEntry>>(
            Path.Combine("services", "Permissions", "persistence.json"));
        Assert.Single(persistence);
        Assert.Equal("PermissionsDbContextApp", persistence[0].DbContext);
        Assert.Contains("PermissionGrant", persistence[0].Entities);
        Assert.Contains("FeatureValue", persistence[0].Entities);
        Assert.DoesNotContain(persistence[0].Entities, entity => entity.StartsWith("EmergencyAccess", StringComparison.Ordinal));
    }

    [Fact]
    public void Absolute_controller_action_routes_replace_controller_prefix()
    {
        var routes = _fixture.ReadJson<List<RouteIndexEntry>>(
            Path.Combine("services", "Identity", "routes.json"));
        Assert.NotEmpty(routes);
        Assert.DoesNotContain(routes, route => route.Route.Contains("~/", StringComparison.Ordinal));
        Assert.Equal(
            "/api/absolute/{id:guid}",
            RouteTemplates.Combine("api/prefixed", "~/api/absolute/{id:guid}"));
    }

    [Fact]
    public void Json_backed_overview_lookup_uses_index_source_and_returns_enriched_fields()
    {
        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, _fixture.IndexPath);
        var overview = service.GetScopeOverview("Identity");
        var identityRoutes = _fixture.ReadJson<List<RouteIndexEntry>>(Path.Combine("services", "Identity", "routes.json"));
        var identityContracts = _fixture.ReadJson<List<ContractIndexEntry>>(Path.Combine("services", "Identity", "contracts.json"));
        var identityPersistence = _fixture.ReadJson<List<PersistenceIndexEntry>>(Path.Combine("services", "Identity", "persistence.json"));
        var identityDependencies = _fixture.ReadJson<ScopeDependenciesEntry>(Path.Combine("services", "Identity", "dependencies.json"));

        Assert.Equal("index", overview.Source);
        Assert.False(overview.IndexAppearsStale);
        Assert.Equal(identityRoutes.Count, overview.Overview.RouteCount);
        Assert.Equal(identityContracts.Count, overview.Overview.ContractCount);
        Assert.Equal("index/services/Identity/projects.json", overview.Overview.ProjectsPath);
        Assert.Equal("index/services/Identity/persistence.json", overview.Overview.PersistencePath);
        Assert.Equal("index/services/Identity/dependencies.json", overview.Overview.DependenciesPath);
        Assert.Equal(identityPersistence.Sum(static entry => entry.Entities.Count), overview.Overview.EntityCount);
        Assert.Equal(
            identityDependencies.SharedModulesUsed.Select(static entry => entry.Module),
            overview.Overview.SharedModulesUsed.Select(static entry => entry.Module));
        Assert.Equal(
            identityDependencies.CrossScopeDependencies.Select(static entry => entry.Scope),
            overview.Overview.CrossScopeDependencies);
    }

    [Fact]
    public void Get_scope_persistence_returns_index_backed_data()
    {
        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, _fixture.IndexPath);
        var expected = _fixture.ReadJson<List<PersistenceIndexEntry>>(Path.Combine("services", "Identity", "persistence.json"));
        var persistence = service.GetScopePersistence("Identity");

        Assert.Equal("index", persistence.Source);
        Assert.NotEmpty(persistence.Persistence);
        Assert.Equal(expected.Select(static entry => entry.DbContext), persistence.Persistence.Select(static entry => entry.DbContext));
    }

    [Fact]
    public void Get_scope_dependencies_returns_index_backed_data()
    {
        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, _fixture.IndexPath);
        var expected = _fixture.ReadJson<ScopeDependenciesEntry>(Path.Combine("services", "Identity", "dependencies.json"));
        var dependencies = service.GetScopeDependencies("Identity");

        Assert.Equal("index", dependencies.Source);
        Assert.Equal(expected.Scope, dependencies.Dependencies.Scope);
        Assert.Equal(expected.ProjectReferences.Count, dependencies.Dependencies.ProjectReferences.Count);
        Assert.Equal(expected.SharedModulesUsed.Select(static entry => entry.Module), dependencies.Dependencies.SharedModulesUsed.Select(static entry => entry.Module));
    }

    [Fact]
    public void List_shared_modules_can_filter_by_service()
    {
        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, _fixture.IndexPath);
        var sharedModules = service.ListSharedModules(usedByService: "Identity");

        Assert.Equal("index", sharedModules.Source);
        Assert.NotEmpty(sharedModules.Modules);
        Assert.All(sharedModules.Modules, module => Assert.Contains("Identity", module.UsedByServices));
        Assert.Contains(sharedModules.Modules, module => module.Module == "Shared.Caching");
    }

    [Fact]
    public void Route_and_contract_queries_stay_inside_requested_scope()
    {
        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, _fixture.IndexPath);
        var routes = service.FindRoutes("email-verification", scope: "Identity");
        var contracts = service.ListScopeContracts("Identity");

        Assert.NotEmpty(routes.Routes);
        Assert.All(routes.Routes, route => Assert.Equal("Identity", route.Service));
        Assert.NotEmpty(contracts.Contracts);
        Assert.All(contracts.Contracts, contract => Assert.Equal("Identity", contract.Service));
    }

    [Fact]
    public void Auto_falls_back_to_live_when_overview_file_is_missing()
    {
        var clonedIndex = CloneIndex();
        File.Delete(Path.Combine(clonedIndex, "services", "Identity", "overview.json"));

        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, clonedIndex);
        var overview = service.GetScopeOverview("Identity");

        Assert.Equal("live-fallback", overview.Source);
        Assert.Equal("Identity", overview.Overview.Scope);
    }

    [Fact]
    public void Auto_uses_index_when_manifest_is_stale_but_index_data_exists()
    {
        var clonedIndex = CloneIndex();
        var manifestPath = Path.Combine(clonedIndex, "manifest.json");
        var manifest = JsonSerializer.Deserialize<ManifestIndexEntry>(File.ReadAllText(manifestPath), RepoIndexBuilder.JsonOptions)
            ?? throw new InvalidOperationException("Could not load manifest.");
        var staleManifest = manifest with { GitCommit = "STALE-COMMIT" };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(staleManifest, RepoIndexBuilder.JsonOptions) + Environment.NewLine);

        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, clonedIndex);
        var overview = service.GetScopeOverview("Identity");
        var scopes = service.ListScopes();
        var persistence = service.GetScopePersistence("Identity");
        var dependencies = service.GetScopeDependencies("Identity");

        Assert.Equal("index", overview.Source);
        Assert.True(overview.IndexAppearsStale);
        Assert.Equal("index", scopes.Source);
        Assert.True(scopes.IndexAppearsStale);
        Assert.Equal("index", persistence.Source);
        Assert.True(persistence.IndexAppearsStale);
        Assert.Equal("index", dependencies.Source);
        Assert.True(dependencies.IndexAppearsStale);
    }

    [Fact]
    public void Auto_falls_back_to_live_when_routes_file_is_missing()
    {
        var clonedIndex = CloneIndex();
        File.Delete(Path.Combine(clonedIndex, "services", "Identity", "routes.json"));

        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, clonedIndex);
        var routes = service.ListScopeRoutes("Identity");

        Assert.Equal("live-fallback", routes.Source);
        Assert.NotEmpty(routes.Routes);
        Assert.All(routes.Routes, route => Assert.Equal("Identity", route.Service));
    }

    [Fact]
    public void Auto_falls_back_to_live_when_contracts_file_is_missing()
    {
        var clonedIndex = CloneIndex();
        File.Delete(Path.Combine(clonedIndex, "services", "Identity", "contracts.json"));

        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, clonedIndex);
        var contracts = service.ListScopeContracts("Identity");

        Assert.Equal("live-fallback", contracts.Source);
        Assert.NotEmpty(contracts.Contracts);
        Assert.All(contracts.Contracts, contract => Assert.Equal("Identity", contract.Service));
    }

    [Fact]
    public void Auto_falls_back_to_live_when_scope_persistence_file_is_missing()
    {
        var clonedIndex = CloneIndex();
        File.Delete(Path.Combine(clonedIndex, "services", "Identity", "persistence.json"));

        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, clonedIndex);
        var persistence = service.GetScopePersistence("Identity");

        Assert.Equal("live-fallback", persistence.Source);
        Assert.NotEmpty(persistence.Persistence);
        Assert.All(persistence.Persistence, entry => Assert.Equal("Identity", entry.Service));
    }

    [Fact]
    public void Auto_falls_back_to_live_when_scope_dependencies_file_is_missing()
    {
        var clonedIndex = CloneIndex();
        File.Delete(Path.Combine(clonedIndex, "services", "Identity", "dependencies.json"));

        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, clonedIndex);
        var dependencies = service.GetScopeDependencies("Identity");

        Assert.Equal("live-fallback", dependencies.Source);
        Assert.Equal("Identity", dependencies.Dependencies.Scope);
        Assert.NotEmpty(dependencies.Dependencies.ProjectReferences);
    }

    [Fact]
    public void Explicit_live_bypasses_index_when_index_data_exists()
    {
        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, _fixture.IndexPath);
        var overview = service.GetScopeOverview("Identity", "live");

        Assert.Equal("live", overview.Source);
        Assert.Equal("Identity", overview.Overview.Scope);
    }

    [Fact]
    public void Explicit_live_bypasses_index_for_new_scope_queries()
    {
        var service = new RepoIndexQueryService(_fixture.RepoRoot, _fixture.SolutionPath, _fixture.IndexPath);
        var persistence = service.GetScopePersistence("Identity", "live");
        var dependencies = service.GetScopeDependencies("Identity", "live");
        var sharedModules = service.ListSharedModules("live", "Identity");

        Assert.Equal("live", persistence.Source);
        Assert.Equal("live", dependencies.Source);
        Assert.Equal("live", sharedModules.Source);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void CliArguments_detect_help_switches(string argument)
    {
        Assert.True(CliArguments.IsHelpRequest([argument]));
    }

    [Fact]
    public void CliArguments_usage_includes_expected_switches()
    {
        var usage = CliArguments.GetUsage("RepoIndexMcp");

        Assert.Contains("Usage: RepoIndexMcp", usage, StringComparison.Ordinal);
        Assert.Contains("--repo-root <path>", usage, StringComparison.Ordinal);
        Assert.Contains("--solution <path>", usage, StringComparison.Ordinal);
        Assert.Contains("--index <path>", usage, StringComparison.Ordinal);
        Assert.Contains("--help, -h, /?", usage, StringComparison.Ordinal);
    }

    private string CloneIndex()
    {
        var clonePath = Path.Combine(Path.GetTempPath(), "tenantwright-repo-index-mcp-test-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(_fixture.IndexPath, clonePath);
        return clonePath;
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(sourcePath, destinationPath, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var destinationFile = file.Replace(sourcePath, destinationPath, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }
}

public sealed class RepoIndexFixture : IDisposable
{
    public RepoIndexFixture()
    {
        RepoRoot = ResolveRepoRoot();
        SolutionPath = RepositoryPaths.ResolveSolutionPath(RepoRoot, null);
        IndexPath = Path.Combine(Path.GetTempPath(), "tenantwright-repo-index-test-" + Guid.NewGuid().ToString("N"));

        var snapshot = new RepoIndexBuilder(RepoRoot, SolutionPath).Build();
        RepoIndexWriter.WriteAsync(IndexPath, snapshot).GetAwaiter().GetResult();
    }

    public string RepoRoot { get; }

    public string SolutionPath { get; }

    public string IndexPath { get; }

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var testsDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Could not resolve the test source directory.");
        var repoRootCandidate = Path.GetFullPath(Path.Combine(testsDirectory, "..", ".."));
        return RepositoryPaths.ResolveRepoRoot(repoRootCandidate);
    }

    public T ReadJson<T>(string relativePath)
    {
        var path = Path.Combine(IndexPath, RepositoryPaths.ToPlatformPath(relativePath.Replace('\\', '/')));
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), RepoIndexBuilder.JsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize '{relativePath}'.");
    }

    public void Dispose()
    {
        if (Directory.Exists(IndexPath))
        {
            Directory.Delete(IndexPath, recursive: true);
        }
    }
}
