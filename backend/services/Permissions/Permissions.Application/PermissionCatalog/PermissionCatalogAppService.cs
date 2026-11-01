using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Definitions;
using Permissions.Domain.Catalog;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Permissions.Application.PermissionCatalog;

public class PermissionCatalogAppService : IPermissionCatalogAppService, ITransientDependency
{
    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;
    private readonly PermissionCatalogManager _manager;
    private readonly PermissionDefinitionManager _definitions;
    private readonly IRepository<PermissionNode, Guid> _repository;
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly IRepository<PermissionGrantChangeRequest, Guid> _requestRepository;
    private readonly TenantDatabaseFanOut _tenantDatabases;

    public PermissionCatalogAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        IDataFilter dataFilter,
        PermissionCatalogManager manager,
        PermissionDefinitionManager definitions,
        IRepository<PermissionNode, Guid> repository,
        IRepository<PermissionGrant, Guid> grantRepository,
        IRepository<PermissionGrantChangeRequest, Guid> requestRepository,
        TenantDatabaseFanOut tenantDatabases)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _dataFilter = dataFilter;
        _manager = manager;
        _definitions = definitions;
        _repository = repository;
        _grantRepository = grantRepository;
        _requestRepository = requestRepository;
        _tenantDatabases = tenantDatabases;
    }

    public async Task<GetPermissionCatalogResultDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadPermissionCatalog, cancellationToken);
        EnsureHostContext();

        var dynamicQuery = await _repository.GetQueryableAsync();
        var dynamicEntries = await dynamicQuery
            .Where(node => node.TenantId == null)
            .AsNoTracking()
            .OrderBy(node => node.SortOrder)
            .ThenBy(node => node.Name)
            .ToListAsync(cancellationToken);

        var flat = StaticEntries().Concat(dynamicEntries.Select(ToDto)).ToList();

        // Attach impact counts in bulk (a fixed number of grouped aggregates, independent of the
        // catalog size) rather than one round-trip per capability. NestByParent reuses these same
        // entry instances, so assigning here also covers the nested tree.
        var impactByName = await LoadImpactCountsAsync(cancellationToken);
        foreach (var entry in flat)
        {
            if (impactByName.TryGetValue(entry.Name, out var impact))
            {
                entry.ActiveGrantCount = impact.ActiveGrantCount;
                entry.AffectedProviderCount = impact.AffectedProviderCount;
                entry.PendingChangeCount = impact.PendingChangeCount;
            }
        }

        // Prefer the code-defined group display name; dynamic-only groups fall back to the raw name.
        var groupDisplayNames = _definitions.GetGroups()
            .GroupBy(group => group.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().DisplayName ?? group.Key, StringComparer.Ordinal);

        var groups = flat
            .GroupBy(entry => entry.GroupName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var entries = group.ToList();
                return new PermissionCatalogGroupDto
                {
                    Name = group.Key,
                    DisplayName = groupDisplayNames.TryGetValue(group.Key, out var displayName) ? displayName : group.Key,
                    Total = entries.Count,
                    DynamicCount = entries.Count(entry => entry.Source == PermissionCatalogSource.Dynamic),
                    Capabilities = NestByParent(entries),
                };
            })
            .ToList();

        return new GetPermissionCatalogResultDto { Groups = groups };
    }

    /// <summary>
    /// Nests a group's flat entries into a tree via <see cref="PermissionCatalogEntryDto.ParentName"/>.
    /// Each entry's <c>Children</c> is populated; the roots (no in-group parent) are returned, ordered by
    /// sort order then name at every level.
    /// </summary>
    private static List<PermissionCatalogEntryDto> NestByParent(IReadOnlyList<PermissionCatalogEntryDto> entries)
    {
        var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var childrenByParent = entries
            .Where(entry => entry.ParentName is { } parent && names.Contains(parent))
            .GroupBy(entry => entry.ParentName!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        static IEnumerable<PermissionCatalogEntryDto> Ordered(IEnumerable<PermissionCatalogEntryDto> items) =>
            items.OrderBy(entry => entry.SortOrder).ThenBy(entry => entry.Name, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            entry.Children = childrenByParent.TryGetValue(entry.Name, out var children)
                ? Ordered(children).ToList()
                : [];
        }

        var roots = entries.Where(entry => entry.ParentName is not { } parent || !names.Contains(parent));
        return Ordered(roots).ToList();
    }

    [Audited]
    public async Task<PermissionCatalogEntryDto> CreateAsync(
        CreatePermissionCatalogEntryDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.CreatePermissionCatalog, cancellationToken);
        EnsureHostContext();
        EnsureNotStatic(input.Name);

        var node = await _manager.CreateAsync(
            tenantId: null,
            name: input.Name,
            elementType: input.ElementType,
            elementKey: input.ElementKey,
            groupName: input.GroupName,
            displayName: input.DisplayName,
            description: input.Description,
            parentName: input.ParentName,
            isEnabled: input.IsEnabled,
            allowsWildcard: input.AllowsWildcard,
            requiresApproval: input.RequiresApproval,
            requiredFeatures: input.RequiredFeatures,
            allowedProviders: input.AllowedProviders,
            allowedActions: input.AllowedActions,
            sortOrder: input.SortOrder,
            cancellationToken: cancellationToken);

        return ToDto(node);
    }

    [Audited]
    public async Task<PermissionCatalogEntryDto> UpdateAsync(
        Guid id,
        UpdatePermissionCatalogEntryDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.UpdatePermissionCatalog, cancellationToken);
        EnsureHostContext();

        var node = await _manager.UpdateAsync(
            id,
            tenantId: null,
            input.ExpectedVersion,
            input.DisplayName,
            input.Description,
            input.GroupName,
            input.ParentName,
            input.IsEnabled,
            input.AllowsWildcard,
            input.RequiresApproval,
            input.RequiredFeatures,
            input.AllowedProviders,
            input.AllowedActions,
            input.SortOrder,
            cancellationToken);

        return ToDto(node);
    }

    [Audited]
    public async Task DeleteAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.DeletePermissionCatalog, cancellationToken);
        EnsureHostContext();

        var node = await _repository.GetAsync(
            candidate => candidate.Id == id && candidate.TenantId == null,
            includeDetails: false,
            cancellationToken: cancellationToken);
        if (expectedVersion <= 0 || node.Version != expectedVersion)
        {
            throw new PermissionCatalogConcurrencyException(node.Version);
        }

        await _repository.DeleteAsync(node, autoSave: true, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Aggregates the three impact counts for every permission name across all tenants, once per database (the
    /// shared one and every dedicated tenant database), and sums them. The sums are exact: each database counts only
    /// the rows it owns, and a provider tuple carries its TenantId, so no row or provider is counted twice. Names with
    /// no grants or pending requests are simply absent and default to zero.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ImpactCounts>> LoadImpactCountsAsync(
        CancellationToken cancellationToken)
    {
        var perDatabase = await _tenantDatabases.QueryEveryDatabaseAsync<PermissionsDbContextApp, IReadOnlyDictionary<string, ImpactCounts>>(
            "permission-catalog-impact-counts",
            (_, token) => LoadDatabaseImpactCountsAsync(token),
            cancellationToken);

        var totals = new Dictionary<string, ImpactCounts>(StringComparer.Ordinal);
        foreach (var (name, counts) in perDatabase.SelectMany(database => database))
        {
            var total = totals.GetValueOrDefault(name);
            totals[name] = new ImpactCounts(
                total.ActiveGrantCount + counts.ActiveGrantCount,
                total.AffectedProviderCount + counts.AffectedProviderCount,
                total.PendingChangeCount + counts.PendingChangeCount);
        }

        return totals;
    }

    private async Task<IReadOnlyDictionary<string, ImpactCounts>> LoadDatabaseImpactCountsAsync(
        CancellationToken cancellationToken)
    {
        using (_dataFilter.Disable<IMultiTenant>())
        {
            var grantQuery = (await _grantRepository.GetQueryableAsync()).AsNoTracking().WhereOwnedByCurrentDatabase();

            var activeGrantCounts = await grantQuery
                .GroupBy(grant => grant.Name)
                .Select(group => new { Name = group.Key, Count = group.LongCount() })
                .ToListAsync(cancellationToken);

            var providerCounts = await grantQuery
                .Select(grant => new { grant.Name, grant.TenantId, grant.ProviderName, grant.ProviderKey })
                .Distinct()
                .GroupBy(target => target.Name)
                .Select(group => new { Name = group.Key, Count = group.LongCount() })
                .ToListAsync(cancellationToken);

            var pendingCounts = await (await _requestRepository.GetQueryableAsync())
                .AsNoTracking()
                .WhereOwnedByCurrentDatabase()
                .Where(request => request.Status == PermissionGrantChangeRequestStatus.Pending)
                .GroupBy(request => request.PermissionName)
                .Select(group => new { Name = group.Key, Count = group.LongCount() })
                .ToListAsync(cancellationToken);

            var counts = new Dictionary<string, ImpactCounts>(StringComparer.Ordinal);
            foreach (var row in activeGrantCounts)
            {
                counts[row.Name] = counts.GetValueOrDefault(row.Name) with { ActiveGrantCount = row.Count };
            }
            foreach (var row in providerCounts)
            {
                counts[row.Name] = counts.GetValueOrDefault(row.Name) with { AffectedProviderCount = row.Count };
            }
            foreach (var row in pendingCounts)
            {
                counts[row.Name] = counts.GetValueOrDefault(row.Name) with { PendingChangeCount = row.Count };
            }

            return counts;
        }
    }

    private readonly record struct ImpactCounts(
        long ActiveGrantCount,
        long AffectedProviderCount,
        long PendingChangeCount);

    private IEnumerable<PermissionCatalogEntryDto> StaticEntries()
    {
        return _definitions.GetGroups().SelectMany(group =>
            Flatten(group.Permissions).Select(definition => new PermissionCatalogEntryDto
            {
                Name = definition.Name,
                DisplayName = definition.DisplayName,
                Description = null,
                GroupName = group.Name,
                ParentName = definition.ParentName,
                ElementType = definition.ElementType,
                ElementKey = definition.ElementKey,
                IsEnabled = definition.IsEnabled,
                AllowsWildcard = definition.AllowsWildcard,
                RequiresApproval = definition.RequiresApproval,
                RequiredFeatures = [.. definition.RequiredFeatures],
                AllowedProviders = [.. definition.Providers],
                AllowedActions = [.. definition.Actions],
                Source = PermissionCatalogSource.StaticDefinition,
                IsMutable = false
            }));
    }

    private void EnsureNotStatic(string name)
    {
        var normalizedName = name?.Trim();
        if (StaticEntries().Any(entry => string.Equals(entry.Name, normalizedName, StringComparison.Ordinal)))
        {
            throw new SharedConflictException(
                PermissionsErrorCodes.PermissionCatalogStaticDefinitionImmutable,
                "Static permission definitions are immutable.");
        }
    }

    private void EnsureHostContext()
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("The platform permission catalog requires host context.");
        }
    }

    private static IEnumerable<PermissionDefinition> Flatten(IEnumerable<PermissionDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            yield return definition;
            foreach (var child in Flatten(definition.Children))
            {
                yield return child;
            }
        }
    }

    private static PermissionCatalogEntryDto ToDto(PermissionNode node)
    {
        return new PermissionCatalogEntryDto
        {
            Id = node.Id,
            Name = node.Name,
            DisplayName = node.DisplayName,
            Description = node.Description,
            GroupName = node.GroupName,
            ParentName = node.ParentName,
            ElementType = node.ElementType,
            ElementKey = node.ElementKey,
            IsEnabled = node.IsEnabled,
            AllowsWildcard = node.AllowsWildcard,
            RequiresApproval = node.RequiresApproval,
            RequiredFeatures = [.. node.RequiredFeatures],
            AllowedProviders = [.. node.AllowedProviders],
            AllowedActions = [.. node.AllowedActions],
            SortOrder = node.SortOrder,
            Source = PermissionCatalogSource.Dynamic,
            IsMutable = true,
            Version = node.Version
        };
    }
}
