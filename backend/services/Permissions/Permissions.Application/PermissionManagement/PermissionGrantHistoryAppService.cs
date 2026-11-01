using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.PermissionHistory;
using Permissions.Domain.Grants;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Domain;
using Shared.Data.Filtering;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.PermissionManagement;

/// <summary>
/// Read-only query over the append-only permission grant-change history (gap claim 8). Tenant scoping
/// is enforced by the ambient tenant query filter (the same as other grant reads); the endpoint is
/// gated by <see cref="PermissionsPolicies.ReadGrantHistory"/> at the controller.
/// </summary>
public sealed class PermissionGrantHistoryAppService : IPermissionGrantHistoryAppService, ITransientDependency
{
    private readonly IRepository<PermissionGrantHistoryEntry, Guid> _historyRepository;
    private readonly IDataFilter _dataFilter;
    private readonly TenantDatabaseFanOut _tenantDatabases;

    public PermissionGrantHistoryAppService(
        IRepository<PermissionGrantHistoryEntry, Guid> historyRepository,
        IDataFilter dataFilter,
        TenantDatabaseFanOut tenantDatabases)
    {
        _historyRepository = historyRepository;
        _dataFilter = dataFilter;
        _tenantDatabases = tenantDatabases;
    }

    /// <summary>
    /// Reads only the ambient tenant's history through the normal global query filter.
    /// </summary>
    public Task<PermissionGrantHistoryResultDto> GetListAsync(
        GetPermissionGrantHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        return GetListCoreAsync(input, cancellationToken);
    }

    /// <summary>
    /// Reads platform-wide history only for the host-only controller, across the shared database and every dedicated
    /// tenant Permissions database, merged into one page in the same order (newest first). The filter override is
    /// scoped to this method so tenant reads retain their normal isolation behavior.
    /// </summary>
    public async Task<PermissionGrantHistoryResultDto> GetPlatformListAsync(
        GetPermissionGrantHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        input ??= new GetPermissionGrantHistoryInput();
        using (_dataFilter.Disable<IMultiTenant>())
        {
            var page = await _tenantDatabases.QueryPageAsync<PermissionsDbContextApp, PermissionGrantHistoryEntryDto>(
                "permission-grant-history-platform-list",
                Math.Max(0, input.SkipCount),
                Math.Clamp(input.MaxResultCount, 1, 1000),
                NewestFirst,
                async (_, skip, take, token) =>
                {
                    var queryable = (await BuildQueryAsync(input)).WhereOwnedByCurrentDatabase();
                    return new TenantDatabasePage<PermissionGrantHistoryEntryDto>(
                        await queryable.LongCountAsync(token),
                        await ReadPageAsync(queryable, skip, take, token));
                },
                cancellationToken);

            return new PermissionGrantHistoryResultDto
            {
                TotalCount = page.TotalCount,
                Items = page.Items.ToList()
            };
        }
    }

    /// <summary>The page order of every history read: newest first, then id (PostgreSQL uuid order).</summary>
    private static readonly IComparer<PermissionGrantHistoryEntryDto> NewestFirst =
        Comparer<PermissionGrantHistoryEntryDto>.Create((left, right) =>
        {
            var byTime = right.OccurredAtUtc.CompareTo(left.OccurredAtUtc);
            return byTime != 0 ? byTime : right.Id.CompareTo(left.Id);
        });

    private async Task<PermissionGrantHistoryResultDto> GetListCoreAsync(
        GetPermissionGrantHistoryInput input,
        CancellationToken cancellationToken)
    {
        input ??= new GetPermissionGrantHistoryInput();
        var queryable = await BuildQueryAsync(input);
        return new PermissionGrantHistoryResultDto
        {
            TotalCount = await queryable.LongCountAsync(cancellationToken),
            Items = await ReadPageAsync(
                queryable,
                Math.Max(0, input.SkipCount),
                Math.Clamp(input.MaxResultCount, 1, 1000),
                cancellationToken)
        };
    }

    private async Task<IQueryable<PermissionGrantHistoryEntry>> BuildQueryAsync(GetPermissionGrantHistoryInput input)
    {
        var queryable = await _historyRepository.GetQueryableAsync();

        var providerName = input.ProviderName?.Trim();
        if (!string.IsNullOrWhiteSpace(providerName))
        {
            queryable = queryable.Where(x => x.ProviderName == providerName);
        }

        var providerKey = input.ProviderKey?.Trim();
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            queryable = queryable.Where(x => x.ProviderKey == providerKey);
        }

        var permissionName = input.PermissionName?.Trim();
        if (!string.IsNullOrWhiteSpace(permissionName))
        {
            queryable = queryable.Where(x => x.PermissionName == permissionName);
        }

        if (input.ChangeType.HasValue)
        {
            queryable = queryable.Where(x => x.ChangeType == input.ChangeType.Value);
        }

        if (input.ActorUserId.HasValue)
        {
            queryable = queryable.Where(x => x.ActorUserId == input.ActorUserId.Value);
        }

        if (input.FromUtc.HasValue)
        {
            queryable = queryable.Where(x => x.OccurredAtUtc >= input.FromUtc.Value);
        }

        if (input.ToUtc.HasValue)
        {
            queryable = queryable.Where(x => x.OccurredAtUtc <= input.ToUtc.Value);
        }

        return queryable;
    }

    private static Task<List<PermissionGrantHistoryEntryDto>> ReadPageAsync(
        IQueryable<PermissionGrantHistoryEntry> queryable,
        int skip,
        int take,
        CancellationToken cancellationToken) =>
        queryable
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .Select(x => new PermissionGrantHistoryEntryDto
            {
                Id = x.Id,
                TenantId = x.TenantId,
                ProviderName = x.ProviderName,
                ProviderKey = x.ProviderKey,
                PermissionName = x.PermissionName,
                ResourceName = x.ResourceName,
                ResourceKey = x.ResourceKey,
                ResourceOwnerRevision = x.ResourceOwnerRevision,
                ChangeType = x.ChangeType,
                OldEffect = x.OldEffect,
                NewEffect = x.NewEffect,
                ExpiresAtUtc = x.ExpiresAtUtc,
                Reason = x.Reason,
                TicketId = x.TicketId,
                ActorUserId = x.ActorUserId,
                OccurredAtUtc = x.OccurredAtUtc
            })
            .ToListAsync(cancellationToken);
}
