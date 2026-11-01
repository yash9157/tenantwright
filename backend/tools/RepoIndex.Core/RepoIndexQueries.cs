using System.Text.Json;

namespace TenantWright.Tools.RepoIndex;

public sealed class RepoIndexStore
{
    private readonly string _repoRoot;
    private readonly string _indexPath;

    public RepoIndexStore(string repoRoot, string indexPath)
    {
        _repoRoot = repoRoot;
        _indexPath = indexPath;
    }

    public ManifestIndexEntry? TryReadManifest() => TryReadJson<ManifestIndexEntry>("manifest.json");

    public IReadOnlyList<ScopeOverviewManifestEntry> ReadScopeManifest() =>
        TryReadJson<List<ScopeOverviewManifestEntry>>("overviews.json") is { } manifest ? manifest : Array.Empty<ScopeOverviewManifestEntry>();

    public IReadOnlyList<ProjectIndexEntry> ReadProjects() =>
        TryReadJson<List<ProjectIndexEntry>>("projects.json") is { } projects ? projects : Array.Empty<ProjectIndexEntry>();

    public IReadOnlyList<SharedModuleIndexEntry> ReadSharedModules() =>
        TryReadSharedModules() is { } sharedModules ? sharedModules : Array.Empty<SharedModuleIndexEntry>();

    public IReadOnlyList<SharedModuleIndexEntry>? TryReadSharedModules() =>
        TryReadJson<List<SharedModuleIndexEntry>>("shared-modules.json");

    public ScopeOverviewEntry? TryReadScopeOverview(string scope) =>
        TryReadJson<ScopeOverviewEntry>(Path.Combine("services", scope, "overview.json"));

    public IReadOnlyList<ProjectIndexEntry>? TryReadScopeProjects(string scope) =>
        TryReadJson<List<ProjectIndexEntry>>(Path.Combine("services", scope, "projects.json"));

    public IReadOnlyList<RouteIndexEntry>? TryReadScopeRoutes(string scope) =>
        TryReadJson<List<RouteIndexEntry>>(Path.Combine("services", scope, "routes.json"));

    public IReadOnlyList<ContractIndexEntry>? TryReadScopeContracts(string scope) =>
        TryReadJson<List<ContractIndexEntry>>(Path.Combine("services", scope, "contracts.json"));

    public IReadOnlyList<PersistenceIndexEntry> ReadPersistence() =>
        TryReadJson<List<PersistenceIndexEntry>>("persistence.json") is { } persistence ? persistence : Array.Empty<PersistenceIndexEntry>();

    public IReadOnlyList<PersistenceIndexEntry>? TryReadScopePersistence(string scope) =>
        TryReadJson<List<PersistenceIndexEntry>>(Path.Combine("services", scope, "persistence.json"));

    public ScopeDependenciesEntry? TryReadScopeDependencies(string scope) =>
        TryReadJson<ScopeDependenciesEntry>(Path.Combine("services", scope, "dependencies.json"));

    public string? GetNotesPath(string scope) => RepositoryPaths.GetScopeNotesPath(_repoRoot, scope);

    public bool IndexAppearsStale()
    {
        var manifest = TryReadManifest();
        if (manifest is null)
        {
            return true;
        }

        return !string.Equals(manifest.GitCommit, GitMetadata.GetCommitHash(_repoRoot), StringComparison.Ordinal);
    }

    private T? TryReadJson<T>(string relativePath)
    {
        var fullPath = Path.Combine(_indexPath, RepositoryPaths.ToPlatformPath(relativePath.Replace('\\', '/')));
        if (!File.Exists(fullPath))
        {
            return default;
        }

        var json = File.ReadAllText(fullPath);
        return JsonSerializer.Deserialize<T>(json, RepoIndexBuilder.JsonOptions);
    }
}

public sealed class RepoIndexQueryService
{
    private readonly string _repoRoot;
    private readonly string _solutionPath;
    private readonly RepoIndexStore _store;
    private RepoIndexSnapshot? _liveSnapshot;

    public RepoIndexQueryService(string repoRoot, string solutionPath, string indexPath)
    {
        _repoRoot = repoRoot;
        _solutionPath = solutionPath;
        _store = new RepoIndexStore(repoRoot, indexPath);
    }

    public ScopeListResult ListScopes(string source = "auto")
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var scopes = _store.ReadScopeManifest();
            if (scopes.Count > 0 || mode == QuerySourceMode.Index)
            {
                return new ScopeListResult("index", stale, scopes);
            }
        }

        var snapshot = GetLiveSnapshot();
        return new ScopeListResult(GetLiveSourceName(mode), stale, snapshot.OverviewManifest);
    }

    public ScopeOverviewResult GetScopeOverview(string scope, string source = "auto")
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var overview = _store.TryReadScopeOverview(scope);
            if (overview is not null)
            {
                return new ScopeOverviewResult("index", stale, overview, _store.GetNotesPath(scope));
            }

            if (mode == QuerySourceMode.Index)
            {
                throw new InvalidOperationException($"Could not find scope overview for '{scope}' in the checked-in index.");
            }
        }

        var snapshot = GetLiveSnapshot();
        var liveOverview = snapshot.Overviews.FirstOrDefault(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Could not find scope overview for '{scope}'.");

        return new ScopeOverviewResult(GetLiveSourceName(mode), stale, liveOverview, _store.GetNotesPath(scope));
    }

    public ScopeProjectsResult ListScopeProjects(string scope, string source = "auto")
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var projects = _store.TryReadScopeProjects(scope);
            if (projects is not null)
            {
                return new ScopeProjectsResult("index", stale, scope, projects, _store.GetNotesPath(scope));
            }

            if (mode == QuerySourceMode.Index)
            {
                return new ScopeProjectsResult("index", stale, scope, Array.Empty<ProjectIndexEntry>(), _store.GetNotesPath(scope));
            }
        }

        var snapshot = GetLiveSnapshot();
        var liveProjects = snapshot.Projects
            .Where(project => RepositoryPaths.TryGetScopeName(project, out var projectScope) &&
                              string.Equals(projectScope, scope, StringComparison.Ordinal))
            .OrderBy(static project => project.Path, StringComparer.Ordinal)
            .ToList();

        return new ScopeProjectsResult(GetLiveSourceName(mode), stale, scope, liveProjects, _store.GetNotesPath(scope));
    }

    public ScopeRoutesResult ListScopeRoutes(string scope, string source = "auto", string? method = null, string? routeContains = null)
    {
        var routes = ResolveRoutes(scope, source);
        var filtered = routes.Routes
            .Where(route => string.IsNullOrWhiteSpace(method) || string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase))
            .Where(route => string.IsNullOrWhiteSpace(routeContains) || route.Route.Contains(routeContains, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return routes with { Routes = filtered };
    }

    public ScopeContractsResult ListScopeContracts(string scope, string source = "auto", string? kind = null)
    {
        var contracts = ResolveContracts(scope, source);
        var filtered = contracts.Contracts
            .Where(contract => string.IsNullOrWhiteSpace(kind) || string.Equals(contract.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return contracts with { Contracts = filtered };
    }

    public ScopePersistenceResult GetScopePersistence(string scope, string source = "auto")
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var persistence = _store.TryReadScopePersistence(scope);
            if (persistence is not null)
            {
                return new ScopePersistenceResult("index", stale, scope, persistence, _store.GetNotesPath(scope));
            }

            if (mode == QuerySourceMode.Index)
            {
                return new ScopePersistenceResult("index", stale, scope, Array.Empty<PersistenceIndexEntry>(), _store.GetNotesPath(scope));
            }
        }

        var snapshot = GetLiveSnapshot();
        var livePersistence = snapshot.Persistence
            .Where(entry => string.Equals(entry.Service, scope, StringComparison.Ordinal))
            .OrderBy(static entry => entry.DbContext, StringComparer.Ordinal)
            .ToList();

        return new ScopePersistenceResult(GetLiveSourceName(mode), stale, scope, livePersistence, _store.GetNotesPath(scope));
    }

    public ScopeDependenciesResult GetScopeDependencies(string scope, string source = "auto")
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var dependencies = _store.TryReadScopeDependencies(scope);
            if (dependencies is not null)
            {
                return new ScopeDependenciesResult("index", stale, dependencies, _store.GetNotesPath(scope));
            }

            if (mode == QuerySourceMode.Index)
            {
                return new ScopeDependenciesResult("index", stale, CreateEmptyDependencies(scope), _store.GetNotesPath(scope));
            }
        }

        var snapshot = GetLiveSnapshot();
        var liveDependencies = snapshot.ScopeDependencies.FirstOrDefault(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal))
            ?? CreateEmptyDependencies(scope);

        return new ScopeDependenciesResult(GetLiveSourceName(mode), stale, liveDependencies, _store.GetNotesPath(scope));
    }

    public ScopeRoutesResult FindRoutes(string query, string source = "auto", string? scope = null)
    {
        var routes = string.IsNullOrWhiteSpace(scope)
            ? ResolveAllRoutes(source)
            : ResolveRoutes(scope, source);

        var filtered = routes.Routes
            .Where(route => route.Route.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            route.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            route.File.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return routes with
        {
            Scope = string.IsNullOrWhiteSpace(scope) ? "*" : routes.Scope,
            Routes = filtered
        };
    }

    public ScopeContractsResult FindContracts(string query, string source = "auto", string? scope = null)
    {
        var contracts = string.IsNullOrWhiteSpace(scope)
            ? ResolveAllContracts(source)
            : ResolveContracts(scope, source);

        var filtered = contracts.Contracts
            .Where(contract => contract.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               contract.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               contract.File.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return contracts with
        {
            Scope = string.IsNullOrWhiteSpace(scope) ? "*" : contracts.Scope,
            Contracts = filtered
        };
    }

    public SharedModuleListResult ListSharedModules(string source = "index", string? usedByService = null)
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();

        if (CanReadIndex(mode))
        {
            var indexedModules = _store.TryReadSharedModules();
            if (indexedModules is not null)
            {
                return new SharedModuleListResult("index", stale, FilterSharedModules(indexedModules, usedByService));
            }

            if (mode == QuerySourceMode.Index)
            {
                return new SharedModuleListResult("index", stale, Array.Empty<SharedModuleUsageResult>());
            }
        }

        var snapshot = GetLiveSnapshot();
        return new SharedModuleListResult(
            GetLiveSourceName(mode),
            stale,
            FilterSharedModules(snapshot.SharedModules, usedByService));
    }

    public SharedModuleUsageQueryResult GetSharedModuleUsage(string module, string source = "index")
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();

        if (CanReadIndex(mode))
        {
            var indexedModules = _store.TryReadSharedModules();
            var usage = indexedModules?.FirstOrDefault(entry => string.Equals(entry.Module, module, StringComparison.Ordinal));

            if (usage is not null)
            {
                return new SharedModuleUsageQueryResult("index", stale, ToSharedModuleUsageResult(usage));
            }

            if (mode == QuerySourceMode.Index)
            {
                throw new InvalidOperationException(indexedModules is null
                    ? "Could not read shared module inventory from the checked-in index."
                    : $"Could not find shared module '{module}' in the checked-in index.");
            }
        }

        var snapshot = GetLiveSnapshot();
        var liveUsage = snapshot.SharedModules
            .FirstOrDefault(entry => string.Equals(entry.Module, module, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Could not find shared module '{module}'.");

        return new SharedModuleUsageQueryResult(GetLiveSourceName(mode), stale, ToSharedModuleUsageResult(liveUsage));
    }

    private ScopeRoutesResult ResolveRoutes(string scope, string source)
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var routes = _store.TryReadScopeRoutes(scope);
            if (routes is not null)
            {
                return new ScopeRoutesResult("index", stale, scope, routes, _store.GetNotesPath(scope));
            }

            if (mode == QuerySourceMode.Index)
            {
                return new ScopeRoutesResult("index", stale, scope, Array.Empty<RouteIndexEntry>(), _store.GetNotesPath(scope));
            }
        }

        var snapshot = GetLiveSnapshot();
        var liveRoutes = snapshot.Routes
            .Where(route => string.Equals(route.Service, scope, StringComparison.Ordinal))
            .ToList();

        return new ScopeRoutesResult(GetLiveSourceName(mode), stale, scope, liveRoutes, _store.GetNotesPath(scope));
    }

    private ScopeContractsResult ResolveContracts(string scope, string source)
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var contracts = _store.TryReadScopeContracts(scope);
            if (contracts is not null)
            {
                return new ScopeContractsResult("index", stale, scope, contracts, _store.GetNotesPath(scope));
            }

            var overview = _store.TryReadScopeOverview(scope);
            if (overview is not null && overview.ContractCount == 0)
            {
                return new ScopeContractsResult("index", stale, scope, Array.Empty<ContractIndexEntry>(), _store.GetNotesPath(scope));
            }

            if (mode == QuerySourceMode.Index)
            {
                return new ScopeContractsResult("index", stale, scope, Array.Empty<ContractIndexEntry>(), _store.GetNotesPath(scope));
            }
        }

        var snapshot = GetLiveSnapshot();
        var liveContracts = snapshot.Contracts
            .Where(contract => string.Equals(contract.Service, scope, StringComparison.Ordinal))
            .ToList();

        return new ScopeContractsResult(GetLiveSourceName(mode), stale, scope, liveContracts, _store.GetNotesPath(scope));
    }

    private ScopeRoutesResult ResolveAllRoutes(string source)
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var manifest = _store.ReadScopeManifest();
            if (manifest.Count > 0 || mode == QuerySourceMode.Index)
            {
                var allRoutes = manifest
                    .SelectMany(entry => _store.TryReadScopeRoutes(entry.Scope) ?? Array.Empty<RouteIndexEntry>())
                    .ToList();

                return new ScopeRoutesResult("index", stale, "*", allRoutes, null);
            }
        }

        var snapshot = GetLiveSnapshot();
        return new ScopeRoutesResult(GetLiveSourceName(mode), stale, "*", snapshot.Routes, null);
    }

    private ScopeContractsResult ResolveAllContracts(string source)
    {
        var mode = ParseSourceMode(source);
        var stale = _store.IndexAppearsStale();
        if (CanReadIndex(mode))
        {
            var manifest = _store.ReadScopeManifest();
            if (manifest.Count > 0 || mode == QuerySourceMode.Index)
            {
                var allContracts = manifest
                    .SelectMany(entry => _store.TryReadScopeContracts(entry.Scope) ?? Array.Empty<ContractIndexEntry>())
                    .ToList();

                return new ScopeContractsResult("index", stale, "*", allContracts, null);
            }
        }

        var snapshot = GetLiveSnapshot();
        return new ScopeContractsResult(GetLiveSourceName(mode), stale, "*", snapshot.Contracts, null);
    }

    private RepoIndexSnapshot GetLiveSnapshot()
    {
        return _liveSnapshot ??= new RepoIndexBuilder(_repoRoot, _solutionPath).Build();
    }

    private static SharedModuleUsageResult[] FilterSharedModules(
        IReadOnlyList<SharedModuleIndexEntry> sharedModules,
        string? usedByService)
    {
        return sharedModules
            .Where(entry => string.IsNullOrWhiteSpace(usedByService) ||
                            entry.UsedByServices.Contains(usedByService, StringComparer.Ordinal))
            .OrderBy(static entry => entry.Module, StringComparer.Ordinal)
            .Select(ToSharedModuleUsageResult)
            .ToArray();
    }

    private static SharedModuleUsageResult ToSharedModuleUsageResult(SharedModuleIndexEntry entry)
    {
        return new SharedModuleUsageResult(entry.Module, entry.Path, entry.Purpose, entry.UsedByServices);
    }

    private static ScopeDependenciesEntry CreateEmptyDependencies(string scope)
    {
        return new ScopeDependenciesEntry(
            scope,
            Array.Empty<ScopeProjectReferenceEntry>(),
            Array.Empty<ScopeExternalDependencyEntry>(),
            Array.Empty<ScopeSharedModuleEntry>(),
            Array.Empty<ScopeOrchestrationReferenceEntry>());
    }

    private static QuerySourceMode ParseSourceMode(string source)
    {
        return source?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => QuerySourceMode.Auto,
            "index" => QuerySourceMode.Index,
            "live" => QuerySourceMode.Live,
            _ => throw new ArgumentException($"Unsupported source mode '{source}'.")
        };
    }

    private enum QuerySourceMode
    {
        Auto,
        Index,
        Live
    }

    private static bool CanReadIndex(QuerySourceMode mode)
    {
        return mode switch
        {
            QuerySourceMode.Index => true,
            QuerySourceMode.Auto => true,
            _ => false
        };
    }

    private static string GetLiveSourceName(QuerySourceMode mode) =>
        mode == QuerySourceMode.Live ? "live" : "live-fallback";
}
