using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TenantWright.Tools.RepoIndex;

public sealed class RepoIndexBuilder
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Dictionary<string, string> SharedModulePurposes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shared.AspNetCore.Host"] = "ASP.NET Core host bootstrapping and shared API hosting conventions.",
            ["Shared.AuditLogs"] = "Auditing abstractions, audit pipeline helpers, and shared audit primitives.",
            ["Shared.Autofac"] = "Autofac integration and container bootstrapping helpers.",
            ["Shared.BlobStoring"] = "Blob storage abstractions and provider-facing storage helpers.",
            ["Shared.Caching"] = "Caching abstractions, tenant-safe cache helpers, and cache wiring support.",
            ["Shared.CurrentUser"] = "Current-user context access and identity projection helpers.",
            ["Shared.Data"] = "Shared data primitives, connection string helpers, and data access utilities.",
            ["Shared.DistributedLocking"] = "Distributed locking abstractions and lock coordination primitives.",
            ["Shared.Domain"] = "Shared domain base types, entity primitives, and cross-service domain helpers.",
            ["Shared.Domain.Shared"] = "Cross-layer shared domain primitives, constants, and lightweight value types.",
            ["Shared.EntityFrameworkCore"] = "Shared EF Core infrastructure, base DbContext support, and persistence helpers.",
            ["Shared.EventBus"] = "Distributed event bus abstractions, RabbitMQ integration, handler registration, and UoW messaging helpers.",
            ["Shared.EventContracts"] = "Transport-independent distributed contracts, canonical wire metadata, and transport-safe payload types.",
            ["Shared.Modularity"] = "Module composition, startup ordering, and dependency management infrastructure.",
            ["Shared.MultiTenancy"] = "Tenant resolution, tenant context, and multi-tenancy infrastructure support.",
            ["Shared.Permissions"] = "Permission abstractions, shared permission definitions, and authorization support.",
            ["Shared.Security"] = "Security helpers, hashing and crypto utilities, and security-facing shared primitives.",
            ["Shared.Uow"] = "Unit-of-work abstractions, transaction scoping, and coordination helpers."
        };

    private readonly string _repoRoot;
    private readonly string _solutionPath;
    private readonly string _solutionRelativePath;

    public RepoIndexBuilder(string repoRoot, string solutionPath)
    {
        _repoRoot = repoRoot;
        _solutionPath = solutionPath;
        _solutionRelativePath = RepositoryPaths.NormalizeRelative(repoRoot, solutionPath);
    }

    public RepoIndexSnapshot Build()
    {
        var projectPaths = LoadSolutionProjectPaths(_solutionPath);
        var projectInfos = projectPaths
            .Select(CreateProjectInfo)
            .OrderBy(project => project.Path, StringComparer.Ordinal)
            .ToList();

        var mainServices = projectInfos
            .Select(project => project.Service)
            .Where(static service => !string.IsNullOrWhiteSpace(service))
            .Select(static service => service!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static service => service, StringComparer.Ordinal)
            .ToList();

        var projects = projectInfos
            .Select(project => new ProjectIndexEntry(
                project.Name,
                project.Path,
                project.Kind,
                project.Service,
                project.References.OrderBy(static reference => reference, StringComparer.Ordinal).ToArray()))
            .ToList();

        var services = mainServices
            .Select(service => BuildServiceEntry(service, projectInfos))
            .OrderBy(static entry => entry.Service, StringComparer.Ordinal)
            .ToList();

        var sharedModules = BuildSharedModuleEntries(projectInfos)
            .OrderBy(static entry => entry.Module, StringComparer.Ordinal)
            .ToList();

        var routes = BuildRouteEntries(projectInfos)
            .OrderBy(static entry => entry.Service, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Route, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Method, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Symbol, StringComparer.Ordinal)
            .ToList();

        var routeManifest = routes
            .GroupBy(static entry => entry.Service, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new RouteManifestEntry(
                group.Key,
                group.Count(),
                $"index/services/{group.Key}/routes.json"))
            .ToList();

        var contracts = BuildContractEntries(projectInfos)
            .OrderBy(static entry => entry.Service, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(static entry => entry.File, StringComparer.Ordinal)
            .ToList();

        var contractManifest = contracts
            .GroupBy(static entry => entry.Service, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new ContractManifestEntry(
                group.Key,
                group.Count(),
                $"index/services/{group.Key}/contracts.json"))
            .ToList();

        var persistence = BuildPersistenceEntries(projectInfos)
            .OrderBy(static entry => entry.Service, StringComparer.Ordinal)
            .ThenBy(static entry => entry.DbContext, StringComparer.Ordinal)
            .ToList();

        var scopeNames = GetScopeNames(services, projects, routes, contracts, persistence);

        var scopeDependencies = BuildScopeDependencies(scopeNames, projects, sharedModules)
            .OrderBy(static entry => entry.Scope, StringComparer.Ordinal)
            .ToList();

        var overviews = BuildScopeOverviews(scopeNames, projects, services, routes, contracts, persistence, scopeDependencies)
            .OrderBy(static entry => entry.Scope, StringComparer.Ordinal)
            .ToList();

        var overviewManifest = overviews
            .Select(overview => new ScopeOverviewManifestEntry(
                overview.Scope,
                overview.ScopeKind,
                $"index/services/{overview.Scope}/overview.json",
                overview.RouteCount,
                overview.ContractCount))
            .ToList();

        var manifest = new ManifestIndexEntry(
            "1.2.0",
            GitMetadata.GetCommitTimestamp(_repoRoot),
            GitMetadata.GetCommitHash(_repoRoot),
            _solutionRelativePath);

        return new RepoIndexSnapshot(
            manifest,
            projects,
            services,
            sharedModules,
            routeManifest,
            routes,
            contractManifest,
            contracts,
            persistence,
            overviewManifest,
            overviews,
            scopeDependencies);
    }

    public async Task WriteAsync(string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        var snapshot = Build();
        await RepoIndexWriter.WriteAsync(outputPath, snapshot);
    }

    private static string[] LoadSolutionProjectPaths(string solutionPath)
    {
        var document = XDocument.Load(solutionPath);
        return document.Descendants("Project")
            .Select(element => (string?)element.Attribute("Path"))
            .Where(static path => !string.IsNullOrWhiteSpace(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solutionPath)!, path!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private ProjectInfo CreateProjectInfo(string absoluteProjectPath)
    {
        var relativePath = RepositoryPaths.NormalizeRelative(_repoRoot, absoluteProjectPath);
        var document = XDocument.Load(absoluteProjectPath);
        var projectElement = document.Root ?? throw new InvalidOperationException($"Missing root project element for '{relativePath}'.");
        var sdk = (string?)projectElement.Attribute("Sdk") ?? string.Empty;
        var references = projectElement
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(include => RepositoryPaths.NormalizeRelative(_repoRoot, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(absoluteProjectPath)!, include!))))
            .OrderBy(static reference => reference, StringComparer.Ordinal)
            .ToArray();

        return new ProjectInfo(
            Path.GetFileNameWithoutExtension(relativePath),
            relativePath,
            RepositoryPaths.GetProjectKind(relativePath, sdk),
            RepositoryPaths.GetServiceName(relativePath),
            references);
    }

    private ServiceIndexEntry BuildServiceEntry(string service, IReadOnlyCollection<ProjectInfo> projectInfos)
    {
        var serviceProjects = projectInfos
            .Where(project => string.Equals(project.Service, service, StringComparison.Ordinal))
            .ToArray();

        var testProject = RepositoryPaths.FindScopeTestProject(_repoRoot, service);

        return new ServiceIndexEntry(
            service,
            $"services/{service}",
            serviceProjects.FirstOrDefault(static project => string.Equals(project.Kind, "service-host", StringComparison.Ordinal))?.Path,
            serviceProjects.FirstOrDefault(static project => string.Equals(project.Kind, "service-contracts", StringComparison.Ordinal))?.Path,
            serviceProjects.FirstOrDefault(static project => string.Equals(project.Kind, "service-application", StringComparison.Ordinal))?.Path,
            serviceProjects.FirstOrDefault(static project => string.Equals(project.Kind, "service-domain", StringComparison.Ordinal))?.Path,
            serviceProjects.FirstOrDefault(static project => string.Equals(project.Kind, "service-efcore", StringComparison.Ordinal))?.Path,
            testProject is null ? Array.Empty<string>() : new[] { testProject });
    }

    private static IEnumerable<SharedModuleIndexEntry> BuildSharedModuleEntries(IReadOnlyCollection<ProjectInfo> projectInfos)
    {
        var sharedProjects = projectInfos
            .Where(static project => string.Equals(project.Kind, "shared", StringComparison.Ordinal))
            .OrderBy(static project => project.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var sharedProject in sharedProjects)
        {
            var usedByServices = projectInfos
                .Where(project => !string.IsNullOrWhiteSpace(project.Service))
                .Where(project => project.References.Contains(sharedProject.Path, StringComparer.Ordinal))
                .Select(project => project.Service!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static service => service, StringComparer.Ordinal)
                .ToArray();

            yield return new SharedModuleIndexEntry(
                sharedProject.Name,
                RepositoryPaths.GetProjectDirectory(sharedProject.Path),
                SharedModulePurposes.TryGetValue(sharedProject.Name, out var purpose)
                    ? purpose
                    : "Shared infrastructure module.",
                usedByServices);
        }
    }

    private IEnumerable<RouteIndexEntry> BuildRouteEntries(IReadOnlyCollection<ProjectInfo> projectInfos)
    {
        var routeProjects = projectInfos
            .Where(static project => project.Kind is "app" or "service-host" or "gateway")
            .ToArray();

        foreach (var project in routeProjects)
        {
            var projectDirectory = Path.GetDirectoryName(Path.Combine(_repoRoot, RepositoryPaths.ToPlatformPath(project.Path)))!;
            foreach (var sourceFile in RepositoryPaths.EnumerateSourceFiles(projectDirectory))
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(sourceFile), path: sourceFile);
                var root = tree.GetCompilationUnitRoot();
                var relativeFile = RepositoryPaths.NormalizeRelative(_repoRoot, sourceFile);
                var scopeName = RepositoryPaths.GetScopeName(project);

                foreach (var entry in ControllerRouteParser.Parse(relativeFile, scopeName, root))
                {
                    yield return entry;
                }

                foreach (var entry in MinimalApiRouteParser.Parse(relativeFile, scopeName, root))
                {
                    yield return entry;
                }
            }
        }
    }

    private IEnumerable<ContractIndexEntry> BuildContractEntries(IReadOnlyCollection<ProjectInfo> projectInfos)
    {
        var contractProjects = projectInfos
            .Where(static project => string.Equals(project.Kind, "service-contracts", StringComparison.Ordinal))
            .ToArray();

        foreach (var project in contractProjects)
        {
            var projectDirectory = Path.GetDirectoryName(Path.Combine(_repoRoot, RepositoryPaths.ToPlatformPath(project.Path)))!;
            foreach (var sourceFile in RepositoryPaths.EnumerateSourceFiles(projectDirectory))
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(sourceFile), path: sourceFile);
                var root = tree.GetCompilationUnitRoot();
                var relativeFile = RepositoryPaths.NormalizeRelative(_repoRoot, sourceFile);

                foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (!TypeFilters.IsSupportedContractType(type) || !TypeFilters.IsPublic(type))
                    {
                        continue;
                    }

                    yield return new ContractIndexEntry(
                        RepositoryPaths.GetScopeName(project),
                        type.Identifier.ValueText,
                        TypeFilters.GetTypeKind(type),
                        NamespaceReader.GetNamespace(type),
                        relativeFile);
                }
            }
        }
    }

    private IEnumerable<PersistenceIndexEntry> BuildPersistenceEntries(IReadOnlyCollection<ProjectInfo> projectInfos)
    {
        var persistenceProjects = projectInfos
            .Where(static project => project.Kind is "service-efcore" or "app")
            .ToArray();

        foreach (var project in persistenceProjects)
        {
            var projectDirectory = Path.GetDirectoryName(Path.Combine(_repoRoot, RepositoryPaths.ToPlatformPath(project.Path)))!;
            var projectRoots = RepositoryPaths.EnumerateSourceFiles(projectDirectory)
                .Select(sourceFile => CSharpSyntaxTree.ParseText(File.ReadAllText(sourceFile), path: sourceFile).GetCompilationUnitRoot())
                .ToArray();
            var registeredEntities = DbContextFilters.FindRegisteredEntities(projectRoots);

            foreach (var root in projectRoots)
            {
                foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (!DbContextFilters.IsDbContext(classDeclaration))
                    {
                        continue;
                    }

                    var dbSets = classDeclaration.Members
                        .OfType<PropertyDeclarationSyntax>()
                        .Select(DbContextFilters.TryGetDbSetEntity)
                        .Where(static entityName => !string.IsNullOrWhiteSpace(entityName))
                        .Select(static entityName => entityName!)
                        .Concat(registeredEntities)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static entityName => entityName, StringComparer.Ordinal)
                        .ToArray();

                    yield return new PersistenceIndexEntry(
                        RepositoryPaths.GetScopeName(project),
                        classDeclaration.Identifier.ValueText,
                        dbSets,
                        project.Path);
                }
            }
        }
    }

    private static string[] GetScopeNames(
        IReadOnlyList<ServiceIndexEntry> services,
        IReadOnlyList<ProjectIndexEntry> projects,
        IReadOnlyList<RouteIndexEntry> routes,
        IReadOnlyList<ContractIndexEntry> contracts,
        IReadOnlyList<PersistenceIndexEntry> persistence)
    {
        var scopeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in services.Select(static entry => entry.Service))
        {
            scopeNames.Add(scope);
        }

        foreach (var scope in projects
                     .Where(static project => RepositoryPaths.TryGetScopeName(project, out _))
                     .Select(RepositoryPaths.GetScopeName))
        {
            scopeNames.Add(scope);
        }

        foreach (var scope in routes.Select(static entry => entry.Service))
        {
            scopeNames.Add(scope);
        }

        foreach (var scope in contracts.Select(static entry => entry.Service))
        {
            scopeNames.Add(scope);
        }

        foreach (var scope in persistence.Select(static entry => entry.Service))
        {
            scopeNames.Add(scope);
        }

        return scopeNames.OrderBy(static item => item, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<ScopeDependenciesEntry> BuildScopeDependencies(
        IReadOnlyList<string> scopeNames,
        IReadOnlyList<ProjectIndexEntry> projects,
        IReadOnlyList<SharedModuleIndexEntry> sharedModules)
    {
        var runtimeProjectByPath = projects.ToDictionary(static project => project.Path, StringComparer.Ordinal);
        var runtimeScopeByProjectPath = projects
            .Where(static project => RepositoryPaths.TryGetScopeName(project, out _))
            .ToDictionary(static project => project.Path, RepositoryPaths.GetScopeName, StringComparer.Ordinal);
        var sharedModuleByProjectPath = projects
            .Where(static project => string.Equals(project.Kind, "shared", StringComparison.Ordinal))
            .Join(
                sharedModules,
                static project => project.Name,
                static module => module.Module,
                static (project, module) => new KeyValuePair<string, ScopeSharedModuleEntry>(
                    project.Path,
                    new ScopeSharedModuleEntry(module.Module, module.Path, module.Purpose)),
                StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
        var projectsByScope = projects
            .Where(static project => RepositoryPaths.TryGetScopeName(project, out _))
            .GroupBy(project => RepositoryPaths.GetScopeName(project), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        foreach (var scope in scopeNames)
        {
            projectsByScope.TryGetValue(scope, out var scopeProjects);
            scopeProjects ??= Array.Empty<ProjectIndexEntry>();

            var projectReferences = scopeProjects
                .Select(project => new ScopeProjectReferenceEntry(
                    project.Name,
                    project.Path,
                    project.Kind,
                    project.References.OrderBy(static reference => reference, StringComparer.Ordinal).ToArray()))
                .ToArray();

            var crossScopeDependencies = scopeProjects
                .SelectMany(project => project.References.Select(reference => new { ProjectPath = project.Path, ReferencePath = reference }))
                .Where(edge => runtimeScopeByProjectPath.TryGetValue(edge.ReferencePath, out var referencedScope) &&
                               !string.Equals(referencedScope, scope, StringComparison.Ordinal))
                .GroupBy(edge => runtimeScopeByProjectPath[edge.ReferencePath], StringComparer.Ordinal)
                .Select(group => new ScopeExternalDependencyEntry(
                    group.Key,
                    group.Select(static edge => edge.ReferencePath).Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
                    group.Select(static edge => edge.ProjectPath).Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal).ToArray()))
                .OrderBy(static entry => entry.Scope, StringComparer.Ordinal)
                .ToArray();

            var sharedModulesUsed = scopeProjects
                .SelectMany(static project => project.References)
                .Where(reference => sharedModuleByProjectPath.ContainsKey(reference))
                .Select(reference => sharedModuleByProjectPath[reference])
                .GroupBy(static entry => entry.Module, StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(static entry => entry.Module, StringComparer.Ordinal)
                .ToArray();

            var orchestrationReferences = scopeProjects
                .SelectMany(project => project.References.Select(reference => new { ProjectPath = project.Path, ReferencePath = reference }))
                .Where(edge => runtimeProjectByPath.TryGetValue(edge.ReferencePath, out var referencedProject) &&
                               string.Equals(referencedProject.Kind, "orchestration", StringComparison.Ordinal))
                .Select(static edge => new ScopeOrchestrationReferenceEntry(edge.ProjectPath, edge.ReferencePath))
                .OrderBy(static entry => entry.ProjectPath, StringComparer.Ordinal)
                .ThenBy(static entry => entry.ReferencePath, StringComparer.Ordinal)
                .ToArray();

            yield return new ScopeDependenciesEntry(
                scope,
                projectReferences,
                crossScopeDependencies,
                sharedModulesUsed,
                orchestrationReferences);
        }
    }

    private IEnumerable<ScopeOverviewEntry> BuildScopeOverviews(
        IReadOnlyList<string> scopeNames,
        IReadOnlyList<ProjectIndexEntry> projects,
        IReadOnlyList<ServiceIndexEntry> services,
        IReadOnlyList<RouteIndexEntry> routes,
        IReadOnlyList<ContractIndexEntry> contracts,
        IReadOnlyList<PersistenceIndexEntry> persistence,
        IReadOnlyList<ScopeDependenciesEntry> scopeDependencies)
    {
        var serviceByName = services.ToDictionary(static entry => entry.Service, StringComparer.Ordinal);
        var projectsByScope = projects
            .Where(static project => RepositoryPaths.TryGetScopeName(project, out _))
            .GroupBy(project => RepositoryPaths.GetScopeName(project), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var routesByScope = routes
            .GroupBy(static entry => entry.Service, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var contractsByScope = contracts
            .GroupBy(static entry => entry.Service, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var persistenceByScope = persistence
            .GroupBy(static entry => entry.Service, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var dependenciesByScope = scopeDependencies.ToDictionary(static entry => entry.Scope, StringComparer.Ordinal);

        foreach (var scope in scopeNames)
        {
            projectsByScope.TryGetValue(scope, out var scopeProjects);
            serviceByName.TryGetValue(scope, out var serviceEntry);
            routesByScope.TryGetValue(scope, out var scopeRoutes);
            contractsByScope.TryGetValue(scope, out var scopeContracts);
            persistenceByScope.TryGetValue(scope, out var scopePersistence);
            dependenciesByScope.TryGetValue(scope, out var scopeDependencyEntry);

            var scopeKind = serviceEntry is not null
                ? "service"
                : scopeProjects?.FirstOrDefault(project => project.Kind is "app" or "gateway")?.Kind ?? "service";

            var rootPath = serviceEntry?.RootPath
                ?? scopeProjects?.Select(static project => RepositoryPaths.GetRootPathForScope(project.Path, project.Kind))
                    .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path))
                ?? $"services/{scope}";

            var hostProject = serviceEntry?.HostProject
                ?? scopeProjects?.FirstOrDefault(project => project.Kind is "app" or "gateway")?.Path
                ?? scopeProjects?.FirstOrDefault()?.Path;

            var programFile = hostProject is null
                ? null
                : RepositoryPaths.FindPrimaryFile(_repoRoot, hostProject, "Program.cs");

            var testProjects = serviceEntry?.TestProjects ?? RepositoryPaths.GetScopeTestProjects(_repoRoot, scope);

            yield return new ScopeOverviewEntry(
                scope,
                scopeKind,
                rootPath,
                hostProject,
                programFile,
                $"index/services/{scope}/projects.json",
                scopeContracts is { Length: > 0 } ? $"index/services/{scope}/contracts.json" : null,
                scopeRoutes is { Length: > 0 } ? $"index/services/{scope}/routes.json" : null,
                $"index/services/{scope}/persistence.json",
                testProjects,
                scopeRoutes?.Length ?? 0,
                scopeContracts?.Length ?? 0,
                scopePersistence?.Select(static entry => entry.DbContext).OrderBy(static name => name, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
                BuildPrimaryEntrypoints(hostProject, programFile, scopeProjects ?? Array.Empty<ProjectIndexEntry>(), serviceEntry).ToArray(),
                $"index/services/{scope}/dependencies.json",
                scopeDependencyEntry?.SharedModulesUsed ?? Array.Empty<ScopeSharedModuleEntry>(),
                scopeDependencyEntry?.CrossScopeDependencies.Select(static entry => entry.Scope).OrderBy(static name => name, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
                scopePersistence?.Sum(static entry => entry.Entities.Count) ?? 0);
        }
    }

    private IEnumerable<string> BuildPrimaryEntrypoints(
        string? hostProject,
        string? programFile,
        IReadOnlyCollection<ProjectIndexEntry> scopeProjects,
        ServiceIndexEntry? serviceEntry)
    {
        var entrypoints = new List<string>();

        if (!string.IsNullOrWhiteSpace(hostProject))
        {
            entrypoints.Add(hostProject);
        }

        if (!string.IsNullOrWhiteSpace(programFile))
        {
            entrypoints.Add(programFile);
        }

        if (hostProject is not null)
        {
            var controllerRoot = RepositoryPaths.FindPrimaryDirectory(_repoRoot, hostProject, "Controllers");
            if (!string.IsNullOrWhiteSpace(controllerRoot))
            {
                entrypoints.Add(controllerRoot);
            }

            var pagesRoot = RepositoryPaths.FindPrimaryDirectory(_repoRoot, hostProject, "Pages");
            if (!string.IsNullOrWhiteSpace(pagesRoot))
            {
                entrypoints.Add(pagesRoot);
            }
        }

        if (!string.IsNullOrWhiteSpace(serviceEntry?.ContractsProject))
        {
            entrypoints.Add(serviceEntry.ContractsProject);
        }

        if (!string.IsNullOrWhiteSpace(serviceEntry?.ApplicationProject))
        {
            entrypoints.Add(serviceEntry.ApplicationProject);
        }

        if (!string.IsNullOrWhiteSpace(serviceEntry?.EfProject))
        {
            entrypoints.Add(serviceEntry.EfProject);
        }

        foreach (var project in scopeProjects.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            if (entrypoints.Count >= 6)
            {
                break;
            }

            if (project.Kind is "service-contracts" or "service-application" or "service-efcore")
            {
                entrypoints.Add(project.Path);
            }
        }

        return entrypoints
            .Where(static entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.Ordinal)
            .Take(6);
    }
}

public static class RepoIndexWriter
{
    public static async Task WriteAsync(string outputPath, RepoIndexSnapshot snapshot)
    {
        await WriteJsonAsync(Path.Combine(outputPath, "manifest.json"), snapshot.Manifest);
        await WriteJsonAsync(Path.Combine(outputPath, "projects.json"), snapshot.Projects);
        await WriteJsonAsync(Path.Combine(outputPath, "services.json"), snapshot.Services);
        await WriteJsonAsync(Path.Combine(outputPath, "shared-modules.json"), snapshot.SharedModules);
        await WriteJsonAsync(Path.Combine(outputPath, "routes.json"), snapshot.RouteManifest);
        await WriteJsonAsync(Path.Combine(outputPath, "contracts.json"), snapshot.ContractManifest);
        await WriteJsonAsync(Path.Combine(outputPath, "persistence.json"), snapshot.Persistence);
        await WriteJsonAsync(Path.Combine(outputPath, "overviews.json"), snapshot.OverviewManifest);

        foreach (var group in snapshot.Projects
                     .Where(static project => RepositoryPaths.TryGetScopeName(project, out _))
                     .GroupBy(project => RepositoryPaths.GetScopeName(project), StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            await WriteJsonAsync(Path.Combine(outputPath, "services", group.Key, "projects.json"), group.ToList());
        }

        foreach (var group in snapshot.Routes
                     .GroupBy(static entry => entry.Service, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            await WriteJsonAsync(Path.Combine(outputPath, "services", group.Key, "routes.json"), group.ToList());
        }

        foreach (var group in snapshot.Contracts
                     .GroupBy(static entry => entry.Service, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            await WriteJsonAsync(Path.Combine(outputPath, "services", group.Key, "contracts.json"), group.ToList());
        }

        foreach (var overview in snapshot.Overviews.OrderBy(static entry => entry.Scope, StringComparer.Ordinal))
        {
            var scopePersistence = snapshot.Persistence
                .Where(entry => string.Equals(entry.Service, overview.Scope, StringComparison.Ordinal))
                .OrderBy(static entry => entry.DbContext, StringComparer.Ordinal)
                .ToList();
            await WriteJsonAsync(Path.Combine(outputPath, "services", overview.Scope, "persistence.json"), scopePersistence);
        }

        foreach (var dependencies in snapshot.ScopeDependencies.OrderBy(static entry => entry.Scope, StringComparer.Ordinal))
        {
            await WriteJsonAsync(Path.Combine(outputPath, "services", dependencies.Scope, "dependencies.json"), dependencies);
        }

        foreach (var overview in snapshot.Overviews.OrderBy(static entry => entry.Scope, StringComparer.Ordinal))
        {
            await WriteJsonAsync(Path.Combine(outputPath, "services", overview.Scope, "overview.json"), overview);
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, RepoIndexBuilder.JsonOptions);
        await stream.WriteAsync(new byte[] { (byte)'\n' });
    }
}

public static class CliArguments
{
    private static readonly string[] HelpSwitches = ["--help", "-h", "/?"];

    public static bool IsHelpRequest(string[] args)
    {
        return args.Any(argument => HelpSwitches.Contains(argument, StringComparer.OrdinalIgnoreCase));
    }

    public static string GetUsage(string executableName)
    {
        return string.Join(
            Environment.NewLine,
            [
                $"Usage: {executableName} [--repo-root <path>] [--solution <path>] [--index <path>]",
                string.Empty,
                "Options:",
                "  --repo-root <path>  Override the repository root containing TenantWright.slnx.",
                "  --solution <path>   Override the solution path. Relative values resolve from the repo root.",
                "  --index <path>      Override the index output path. Alias: --output.",
                "  --help, -h, /?      Show this help message and exit."
            ]);
    }

    public static CliOptions Parse(string[] args)
    {
        string? repoRoot = null;
        string? solutionPath = null;
        string? outputPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--repo-root":
                    repoRoot = ReadValue(args, ++index, "--repo-root");
                    break;
                case "--solution":
                    solutionPath = ReadValue(args, ++index, "--solution");
                    break;
                case "--output":
                case "--index":
                    outputPath = ReadValue(args, ++index, args[index - 1]);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        return new CliOptions(repoRoot, solutionPath, outputPath);
    }

    private static string ReadValue(string[] args, int index, string optionName)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for '{optionName}'.");
        }

        return args[index];
    }
}

public static class RepositoryPaths
{
    public static string ResolveRepoRoot(string? input)
    {
        if (!string.IsNullOrWhiteSpace(input))
        {
            return Path.GetFullPath(input);
        }

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TenantWright.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to resolve repo root containing TenantWright.slnx.");
    }

    public static string ResolveSolutionPath(string repoRoot, string? input)
    {
        var path = string.IsNullOrWhiteSpace(input)
            ? Path.Combine(repoRoot, "TenantWright.slnx")
            : Path.GetFullPath(Path.IsPathRooted(input) ? input : Path.Combine(repoRoot, input));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Could not find solution file.", path);
        }

        return path;
    }

    public static string ResolveOutputPath(string repoRoot, string? input)
    {
        return string.IsNullOrWhiteSpace(input)
            ? Path.Combine(repoRoot, "index")
            : Path.GetFullPath(Path.IsPathRooted(input) ? input : Path.Combine(repoRoot, input));
    }

    public static string ResolveIndexPath(string repoRoot, string? input) => ResolveOutputPath(repoRoot, input);

    public static string NormalizeRelative(string repoRoot, string path)
    {
        return Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
    }

    public static string ToPlatformPath(string relativePath)
    {
        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    public static string? GetServiceName(string relativeProjectPath)
    {
        var parts = relativeProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && string.Equals(parts[0], "services", StringComparison.Ordinal)
            ? parts[1]
            : null;
    }

    internal static string GetScopeName(ProjectInfo project) => project.Service ?? project.Name;

    public static bool TryGetScopeName(ProjectIndexEntry project, out string scope)
    {
        if (!string.IsNullOrWhiteSpace(project.Service))
        {
            scope = project.Service!;
            return true;
        }

        if (project.Kind is "app" or "gateway")
        {
            scope = project.Name;
            return true;
        }

        scope = string.Empty;
        return false;
    }

    public static string GetScopeName(ProjectIndexEntry project)
    {
        if (TryGetScopeName(project, out var scope))
        {
            return scope;
        }

        throw new InvalidOperationException($"Project '{project.Path}' does not belong to a runtime scope.");
    }

    public static string GetProjectKind(string relativeProjectPath, string sdk)
    {
        if (relativeProjectPath.StartsWith("app/", StringComparison.Ordinal))
        {
            return "app";
        }

        if (relativeProjectPath.StartsWith("gateway/", StringComparison.Ordinal))
        {
            return "gateway";
        }

        if (relativeProjectPath.StartsWith("orchestration/", StringComparison.Ordinal))
        {
            return "orchestration";
        }

        if (relativeProjectPath.StartsWith("shared/", StringComparison.Ordinal))
        {
            return "shared";
        }

        if (relativeProjectPath.StartsWith("tests/", StringComparison.Ordinal))
        {
            return "test";
        }

        if (relativeProjectPath.StartsWith("tools/", StringComparison.Ordinal))
        {
            return "tool";
        }

        var fileName = Path.GetFileNameWithoutExtension(relativeProjectPath);
        if (fileName.EndsWith(".Application.Contracts", StringComparison.Ordinal))
        {
            return "service-contracts";
        }

        if (fileName.EndsWith(".Application", StringComparison.Ordinal))
        {
            return "service-application";
        }

        if (fileName.EndsWith(".Domain.Shared", StringComparison.Ordinal))
        {
            return "service-domain-shared";
        }

        if (fileName.EndsWith(".Domain", StringComparison.Ordinal))
        {
            return "service-domain";
        }

        if (fileName.EndsWith(".EntityFrameworkCore", StringComparison.Ordinal))
        {
            return "service-efcore";
        }

        return sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) ? "service-host" : "service-project";
    }

    public static string GetProjectDirectory(string relativeProjectPath)
    {
        return Path.GetDirectoryName(relativeProjectPath)?.Replace('\\', '/') ?? relativeProjectPath;
    }

    public static string GetRootPathForScope(string relativeProjectPath, string kind)
    {
        var projectDirectory = GetProjectDirectory(relativeProjectPath);
        return kind switch
        {
            "app" => projectDirectory,
            "gateway" => projectDirectory,
            _ => projectDirectory.Contains('/', StringComparison.Ordinal)
                ? projectDirectory[..projectDirectory.IndexOf('/', projectDirectory.IndexOf('/', StringComparison.Ordinal) + 1)]
                : projectDirectory
        };
    }

    public static string? FindScopeTestProject(string repoRoot, string scope)
    {
        var candidate = Path.Combine(repoRoot, "tests", $"{scope}.Tests", $"{scope}.Tests.csproj");
        return File.Exists(candidate) ? NormalizeRelative(repoRoot, candidate) : null;
    }

    public static IReadOnlyList<string> GetScopeTestProjects(string repoRoot, string scope)
    {
        var testProject = FindScopeTestProject(repoRoot, scope);
        return testProject is null ? Array.Empty<string>() : new[] { testProject };
    }

    public static string? FindPrimaryFile(string repoRoot, string projectPath, string fileName)
    {
        var projectDirectory = Path.GetDirectoryName(Path.Combine(repoRoot, ToPlatformPath(projectPath)));
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var candidate = Path.Combine(projectDirectory, fileName);
        return File.Exists(candidate) ? NormalizeRelative(repoRoot, candidate) : null;
    }

    public static string? FindPrimaryDirectory(string repoRoot, string projectPath, string directoryName)
    {
        var projectDirectory = Path.GetDirectoryName(Path.Combine(repoRoot, ToPlatformPath(projectPath)));
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var candidate = Path.Combine(projectDirectory, directoryName);
        return Directory.Exists(candidate) ? NormalizeRelative(repoRoot, candidate) : null;
    }

    public static string? GetScopeNotesPath(string repoRoot, string scope)
    {
        var candidate = Path.Combine(repoRoot, "docs", "services", scope, "notes.md");
        return File.Exists(candidate) ? NormalizeRelative(repoRoot, candidate) : null;
    }

    public static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current, "*.cs"))
            {
                yield return file;
            }
        }
    }
}

public static class GitMetadata
{
    public static string GetCommitHash(string workingDirectory)
    {
        return TryRunGit(workingDirectory, "rev-parse HEAD") ?? "UNKNOWN";
    }

    public static string GetCommitTimestamp(string workingDirectory)
    {
        var commitTimestamp = TryRunGit(workingDirectory, "show -s --format=%cI HEAD");
        if (DateTimeOffset.TryParse(commitTimestamp, out var parsed))
        {
            return parsed.UtcDateTime.ToString("O");
        }

        return DateTime.UtcNow.ToString("O");
    }

    private static string? TryRunGit(string workingDirectory, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return null;
            }

            Task.WhenAll(outputTask, errorTask).Wait(5000);

            var output = outputTask.IsCompletedSuccessfully ? outputTask.Result.Trim() : string.Empty;
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}

public static class ControllerRouteParser
{
    private static readonly Dictionary<string, string> HttpMethodMap = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH"
    };

    public static IEnumerable<RouteIndexEntry> Parse(string relativeFile, string service, CompilationUnitSyntax root)
    {
        foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!IsControllerClass(classDeclaration))
            {
                continue;
            }

            var namespaceName = NamespaceReader.GetNamespace(classDeclaration);
            var classSymbol = string.IsNullOrWhiteSpace(namespaceName)
                ? classDeclaration.Identifier.ValueText
                : $"{namespaceName}.{classDeclaration.Identifier.ValueText}";
            var classRoutes = GetRouteTemplates(classDeclaration.AttributeLists).DefaultIfEmpty(string.Empty).ToArray();

            foreach (var method in classDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                foreach (var attribute in method.AttributeLists.SelectMany(static list => list.Attributes))
                {
                    var attributeName = TypeFilters.GetUnqualifiedName(attribute.Name);
                    if (!HttpMethodMap.TryGetValue(attributeName, out var methodName))
                    {
                        continue;
                    }

                    var methodRoute = AttributeReader.GetFirstArgumentValue(attribute);
                    foreach (var classRoute in classRoutes)
                    {
                        yield return new RouteIndexEntry(
                            service,
                            methodName,
                            RouteTemplates.Combine(classRoute, methodRoute),
                            $"{classSymbol}.{method.Identifier.ValueText}",
                            relativeFile,
                            "controller-action");
                    }
                }
            }
        }
    }

    private static bool IsControllerClass(ClassDeclarationSyntax classDeclaration)
    {
        var hasApiController = classDeclaration.AttributeLists
            .SelectMany(static list => list.Attributes)
            .Any(attribute => string.Equals(TypeFilters.GetUnqualifiedName(attribute.Name), "ApiController", StringComparison.Ordinal));

        var inheritsControllerBase = classDeclaration.BaseList?.Types.Any(baseType =>
            baseType.Type.ToString().Contains("ControllerBase", StringComparison.Ordinal)) == true;

        return hasApiController || inheritsControllerBase;
    }

    private static IEnumerable<string> GetRouteTemplates(SyntaxList<AttributeListSyntax> attributeLists)
    {
        foreach (var attribute in attributeLists.SelectMany(static list => list.Attributes))
        {
            if (!string.Equals(TypeFilters.GetUnqualifiedName(attribute.Name), "Route", StringComparison.Ordinal))
            {
                continue;
            }

            yield return AttributeReader.GetFirstArgumentValue(attribute) ?? string.Empty;
        }
    }
}

public static class MinimalApiRouteParser
{
    private static readonly Dictionary<string, string> HttpMethodMap = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH"
    };

    public static IEnumerable<RouteIndexEntry> Parse(string relativeFile, string service, CompilationUnitSyntax root)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            if (!HttpMethodMap.TryGetValue(methodName, out var httpMethod))
            {
                continue;
            }

            var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault();
            var routeTemplate = firstArgument is null
                ? string.Empty
                : AttributeReader.GetExpressionValue(firstArgument.Expression) ?? firstArgument.Expression.ToString();

            yield return new RouteIndexEntry(
                service,
                httpMethod,
                RouteTemplates.Normalize(routeTemplate),
                $"{Path.GetFileNameWithoutExtension(relativeFile)}::{methodName}",
                relativeFile,
                "minimal-api");
        }
    }
}

public static class RouteTemplates
{
    public static string Combine(string classRoute, string? methodRoute)
    {
        if (!string.IsNullOrWhiteSpace(methodRoute))
        {
            var trimmedMethodRoute = methodRoute.Trim().Trim('"');
            if (trimmedMethodRoute.StartsWith("~/", StringComparison.Ordinal))
            {
                return Normalize(trimmedMethodRoute[1..]);
            }

            if (trimmedMethodRoute.StartsWith('/'))
            {
                return Normalize(trimmedMethodRoute);
            }
        }

        if (string.IsNullOrWhiteSpace(classRoute))
        {
            return Normalize(methodRoute);
        }

        if (string.IsNullOrWhiteSpace(methodRoute))
        {
            return Normalize(classRoute);
        }

        return Normalize($"{classRoute.TrimEnd('/')}/{methodRoute.TrimStart('/')}");
    }

    public static string Normalize(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "/";
        }

        var value = template.Trim().Trim('"');
        return value.StartsWith('/') ? value : $"/{value}";
    }
}

public static class AttributeReader
{
    public static string? GetFirstArgumentValue(AttributeSyntax attribute)
    {
        var expression = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        return expression is null ? null : GetExpressionValue(expression) ?? expression.ToString();
    }

    public static string? GetExpressionValue(ExpressionSyntax expression)
    {
        return expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            InterpolatedStringExpressionSyntax interpolated => interpolated.ToString(),
            _ => null
        };
    }
}

public static class DbContextFilters
{
    public static IReadOnlyList<string> FindRegisteredEntities(IEnumerable<CompilationUnitSyntax> roots)
    {
        var entities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var genericName = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax { Name: GenericNameSyntax memberGeneric } => memberGeneric,
                    GenericNameSyntax directGeneric => directGeneric,
                    _ => null
                };

                if (genericName is not null &&
                    string.Equals(genericName.Identifier.ValueText, "Entity", StringComparison.Ordinal) &&
                    genericName.TypeArgumentList.Arguments.Count == 1)
                {
                    var typeArgument = genericName.TypeArgumentList.Arguments[0];
                    var entityName = TypeFilters.GetUnqualifiedName(typeArgument);
                    var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var isMethodTypeParameter = containingMethod?.TypeParameterList?.Parameters.Any(
                        parameter => string.Equals(parameter.Identifier.ValueText, entityName, StringComparison.Ordinal)) == true;
                    if (!isMethodTypeParameter)
                    {
                        entities.Add(entityName);
                    }
                }
            }

            foreach (var typeOfExpression in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
            {
                var registrationField = typeOfExpression.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
                var registrationProperty = typeOfExpression.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                var registrationName = registrationField?.Identifier.ValueText ?? registrationProperty?.Identifier.ValueText;
                if (registrationName is not null &&
                    (registrationName.EndsWith("Entities", StringComparison.Ordinal) ||
                     registrationName.EndsWith("EntityTypes", StringComparison.Ordinal) ||
                     registrationName.EndsWith("EntityRegistrations", StringComparison.Ordinal)))
                {
                    entities.Add(TypeFilters.GetUnqualifiedName(typeOfExpression.Type));
                }
            }
        }

        return entities.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
    }

    public static bool IsDbContext(ClassDeclarationSyntax classDeclaration)
    {
        var className = classDeclaration.Identifier.ValueText;
        if (className.EndsWith("DbContext", StringComparison.Ordinal) ||
            className.EndsWith("DbContextApp", StringComparison.Ordinal))
        {
            return true;
        }

        return classDeclaration.BaseList?.Types.Any(baseType =>
        {
            var baseTypeName = TypeFilters.GetUnqualifiedName(baseType.Type);
            return string.Equals(baseTypeName, "DbContext", StringComparison.Ordinal) ||
                   string.Equals(baseTypeName, "SharedDbContext", StringComparison.Ordinal);
        }) == true;
    }

    public static string? TryGetDbSetEntity(PropertyDeclarationSyntax property)
    {
        return property.Type switch
        {
            GenericNameSyntax genericName when string.Equals(genericName.Identifier.ValueText, "DbSet", StringComparison.Ordinal) =>
                genericName.TypeArgumentList.Arguments.FirstOrDefault()?.ToString(),
            QualifiedNameSyntax qualifiedName when qualifiedName.Right is GenericNameSyntax genericName &&
                                               string.Equals(genericName.Identifier.ValueText, "DbSet", StringComparison.Ordinal) =>
                genericName.TypeArgumentList.Arguments.FirstOrDefault()?.ToString(),
            _ => null
        };
    }
}

public static class TypeFilters
{
    public static bool IsSupportedContractType(BaseTypeDeclarationSyntax declaration)
    {
        return declaration is ClassDeclarationSyntax or InterfaceDeclarationSyntax or RecordDeclarationSyntax;
    }

    public static bool IsPublic(MemberDeclarationSyntax declaration)
    {
        return declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
    }

    public static string GetTypeKind(BaseTypeDeclarationSyntax declaration)
    {
        return declaration switch
        {
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax => "record",
            _ => "class"
        };
    }

    public static string GetUnqualifiedName(NameSyntax nameSyntax)
    {
        return nameSyntax switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
            _ => nameSyntax.ToString()
        };
    }

    public static string GetUnqualifiedName(TypeSyntax typeSyntax)
    {
        return typeSyntax switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            NullableTypeSyntax nullable => GetUnqualifiedName(nullable.ElementType),
            _ => typeSyntax.ToString()
        };
    }
}

public static class NamespaceReader
{
    public static string GetNamespace(SyntaxNode node)
    {
        return node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(static namespaceDeclaration => namespaceDeclaration.Name.ToString())
            .FirstOrDefault() ?? string.Empty;
    }
}

internal sealed record ProjectInfo(
    string Name,
    string Path,
    string Kind,
    string? Service,
    IReadOnlyList<string> References);
