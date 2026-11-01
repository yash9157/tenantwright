namespace TenantWright.Tools.RepoIndex;

public sealed record CliOptions(string? RepoRoot, string? SolutionPath, string? OutputPath);

public sealed record ManifestIndexEntry(
    string SchemaVersion,
    string GeneratedAtUtc,
    string GitCommit,
    string SolutionPath);

public sealed record ProjectIndexEntry(
    string Name,
    string Path,
    string Kind,
    string? Service,
    IReadOnlyList<string> References);

public sealed record ServiceIndexEntry(
    string Service,
    string RootPath,
    string? HostProject,
    string? ContractsProject,
    string? ApplicationProject,
    string? DomainProject,
    string? EfProject,
    IReadOnlyList<string> TestProjects);

public sealed record SharedModuleIndexEntry(
    string Module,
    string Path,
    string Purpose,
    IReadOnlyList<string> UsedByServices);

public sealed record RouteIndexEntry(
    string Service,
    string Method,
    string Route,
    string Symbol,
    string File,
    string HandlerKind);

public sealed record RouteManifestEntry(
    string Service,
    int Count,
    string Path);

public sealed record ContractIndexEntry(
    string Service,
    string Name,
    string Kind,
    string Namespace,
    string File);

public sealed record ContractManifestEntry(
    string Service,
    int Count,
    string Path);

public sealed record PersistenceIndexEntry(
    string Service,
    string DbContext,
    IReadOnlyList<string> Entities,
    string MigrationsProject);

public sealed record ScopeSharedModuleEntry(
    string Module,
    string Path,
    string Purpose);

public sealed record ScopeProjectReferenceEntry(
    string Project,
    string Path,
    string Kind,
    IReadOnlyList<string> References);

public sealed record ScopeExternalDependencyEntry(
    string Scope,
    IReadOnlyList<string> ReferencedProjects,
    IReadOnlyList<string> ReferencedByProjects);

public sealed record ScopeOrchestrationReferenceEntry(
    string ProjectPath,
    string ReferencePath);

public sealed record ScopeDependenciesEntry(
    string Scope,
    IReadOnlyList<ScopeProjectReferenceEntry> ProjectReferences,
    IReadOnlyList<ScopeExternalDependencyEntry> CrossScopeDependencies,
    IReadOnlyList<ScopeSharedModuleEntry> SharedModulesUsed,
    IReadOnlyList<ScopeOrchestrationReferenceEntry> OrchestrationReferences);

public sealed record ScopeOverviewEntry(
    string Scope,
    string ScopeKind,
    string RootPath,
    string? HostProject,
    string? ProgramFile,
    string ProjectsPath,
    string? ContractsPath,
    string? RoutesPath,
    string? PersistencePath,
    IReadOnlyList<string> TestProjects,
    int RouteCount,
    int ContractCount,
    IReadOnlyList<string> DbContexts,
    IReadOnlyList<string> PrimaryEntrypoints,
    string DependenciesPath,
    IReadOnlyList<ScopeSharedModuleEntry> SharedModulesUsed,
    IReadOnlyList<string> CrossScopeDependencies,
    int EntityCount);

public sealed record ScopeOverviewManifestEntry(
    string Scope,
    string ScopeKind,
    string Path,
    int RouteCount,
    int ContractCount);

public sealed record RepoIndexSnapshot(
    ManifestIndexEntry Manifest,
    IReadOnlyList<ProjectIndexEntry> Projects,
    IReadOnlyList<ServiceIndexEntry> Services,
    IReadOnlyList<SharedModuleIndexEntry> SharedModules,
    IReadOnlyList<RouteManifestEntry> RouteManifest,
    IReadOnlyList<RouteIndexEntry> Routes,
    IReadOnlyList<ContractManifestEntry> ContractManifest,
    IReadOnlyList<ContractIndexEntry> Contracts,
    IReadOnlyList<PersistenceIndexEntry> Persistence,
    IReadOnlyList<ScopeOverviewManifestEntry> OverviewManifest,
    IReadOnlyList<ScopeOverviewEntry> Overviews,
    IReadOnlyList<ScopeDependenciesEntry> ScopeDependencies);

public sealed record SharedModuleUsageResult(
    string Module,
    string Path,
    string Purpose,
    IReadOnlyList<string> UsedByServices);

public sealed record ScopeListResult(
    string Source,
    bool IndexAppearsStale,
    IReadOnlyList<ScopeOverviewManifestEntry> Scopes);

public sealed record ScopeOverviewResult(
    string Source,
    bool IndexAppearsStale,
    ScopeOverviewEntry Overview,
    string? NotesPath);

public sealed record ScopeProjectsResult(
    string Source,
    bool IndexAppearsStale,
    string Scope,
    IReadOnlyList<ProjectIndexEntry> Projects,
    string? NotesPath);

public sealed record ScopeRoutesResult(
    string Source,
    bool IndexAppearsStale,
    string Scope,
    IReadOnlyList<RouteIndexEntry> Routes,
    string? NotesPath);

public sealed record ScopeContractsResult(
    string Source,
    bool IndexAppearsStale,
    string Scope,
    IReadOnlyList<ContractIndexEntry> Contracts,
    string? NotesPath);

public sealed record ScopePersistenceResult(
    string Source,
    bool IndexAppearsStale,
    string Scope,
    IReadOnlyList<PersistenceIndexEntry> Persistence,
    string? NotesPath);

public sealed record ScopeDependenciesResult(
    string Source,
    bool IndexAppearsStale,
    ScopeDependenciesEntry Dependencies,
    string? NotesPath);

public sealed record SharedModuleListResult(
    string Source,
    bool IndexAppearsStale,
    IReadOnlyList<SharedModuleUsageResult> Modules);

public sealed record SharedModuleUsageQueryResult(
    string Source,
    bool IndexAppearsStale,
    SharedModuleUsageResult Usage);
