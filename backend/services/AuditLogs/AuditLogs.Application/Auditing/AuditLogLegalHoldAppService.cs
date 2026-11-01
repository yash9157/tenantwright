using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace AuditLogs.Application.Auditing;

/// <summary>
/// CRUD surface for legal holds (sub-flow 5). Host-only (holds are a cross-tenant compliance tool,
/// mirroring the archive management host-only pattern) - a hold scoped to a specific tenant can be
/// created by a host user targeting that tenant, but tenant users cannot self-serve holds.
/// </summary>
[ExposeServices(typeof(IAuditLogLegalHoldAppService))]
public sealed class AuditLogLegalHoldAppService : IAuditLogLegalHoldAppService, ITransientDependency
{
    private readonly IRepository<AuditLogLegalHold, long> _repository;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IDataFilter _dataFilter;

    public AuditLogLegalHoldAppService(
        IRepository<AuditLogLegalHold, long> repository,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        IDataFilter dataFilter)
    {
        _repository = repository;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _dataFilter = dataFilter;
    }

    public async Task<AuditLogLegalHoldDto> CreateAsync(CreateAuditLogLegalHoldInput input)
    {
        EnsureHostAccess();
        ArgumentNullException.ThrowIfNull(input);

        var hold = new AuditLogLegalHold(
            id: 0,
            name: input.Name,
            tenantId: input.TenantId,
            fromUtc: input.FromUtc,
            toUtc: input.ToUtc,
            userId: input.UserId,
            reason: input.Reason);

        await _repository.InsertAsync(hold, autoSave: true);
        return MapToDto(hold);
    }

    public async Task<AuditLogLegalHoldDto> ReleaseAsync(long id, ReleaseAuditLogLegalHoldInput input)
    {
        EnsureHostAccess();

        var hold = await _repository.GetAsync(id);
        hold.Release(input?.Reason);
        await _repository.UpdateAsync(hold, autoSave: true);
        return MapToDto(hold);
    }

    public async Task<PagedResultDto<AuditLogLegalHoldDto>> GetListAsync(GetAuditLogLegalHoldsInput input)
    {
        EnsureHostAccess();
        input ??= new GetAuditLogLegalHoldsInput();
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var skipCount = Math.Max(0, input.SkipCount);

        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var query = await _repository.GetQueryableAsync();
            if (input.IsReleased.HasValue)
            {
                query = query.Where(x => x.IsReleased == input.IsReleased.Value);
            }

            if (input.TenantId.HasValue)
            {
                query = query.Where(x => x.TenantId == input.TenantId.Value);
            }

            var totalCount = await query.LongCountAsync();
            var holds = await query
                .OrderByDescending(x => x.CreationTime)
                .Skip(skipCount)
                .Take(maxResultCount)
                .ToListAsync();

            return new PagedResultDto<AuditLogLegalHoldDto>
            {
                TotalCount = totalCount,
                Items = holds.Select(MapToDto).ToList()
            };
        });
    }

    private void EnsureHostAccess()
    {
        if (_currentTenant.Id.HasValue || _currentUser.TenantId.HasValue)
        {
            throw new UnauthorizedAccessException("Legal hold management is only available to host operators.");
        }
    }

    // Shared-database-only: legal holds are a host-operated registry created in host scope, and their only enforcement
    // point is the shared-database archive purge (dedicated tenant databases are never archived or purged).
    private async Task<T> ExecuteWithHostMultiTenantFilterDisabledAsync<T>(Func<Task<T>> action)
    {
        using (_dataFilter.Disable<IMultiTenant>())
        {
            return await action();
        }
    }

    private static AuditLogLegalHoldDto MapToDto(AuditLogLegalHold hold)
    {
        return new AuditLogLegalHoldDto
        {
            Id = hold.Id,
            Name = hold.Name,
            TenantId = hold.TenantId,
            FromUtc = hold.FromUtc,
            ToUtc = hold.ToUtc,
            UserId = hold.UserId,
            Reason = hold.Reason,
            IsReleased = hold.IsReleased,
            ReleasedAtUtc = hold.ReleasedAtUtc,
            CreationTime = hold.CreationTime
        };
    }
}
