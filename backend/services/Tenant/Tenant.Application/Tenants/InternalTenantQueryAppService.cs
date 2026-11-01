using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Tenant.Application.Contracts.Tenants;
using Tenant.Domain.Shared.Enums;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

/// <summary>
/// Owns non-sensitive internal Tenant projections behind a focused application boundary.
/// HTTP callers receive their unit of work from the shared MVC request convention.
/// </summary>
public sealed class InternalTenantQueryAppService :
    IInternalTenantQueryAppService,
    ITransientDependency
{
    private const int MaximumServiceabilityBatchSize = 200;

    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;

    public InternalTenantQueryAppService(IDbContextProvider<TenantDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task<TenantConfigurationDto?> GetConfigurationByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Name,
                candidate.IsActive
            })
            .FirstOrDefaultAsync(cancellationToken);

        return tenant == null ? null : MapConfiguration(tenant.Id, tenant.Name, tenant.IsActive);
    }

    public async Task<TenantConfigurationDto?> GetConfigurationByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .Where(candidate => candidate.NormalizedName == normalizedName)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Name,
                candidate.IsActive
            })
            .FirstOrDefaultAsync(cancellationToken);

        return tenant == null ? null : MapConfiguration(tenant.Id, tenant.Name, tenant.IsActive);
    }

    public async Task<TenantServiceabilityBatchResult> GetServiceabilityBatchAsync(
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);

        var distinctTenantIds = tenantIds
            .Where(tenantId => tenantId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (distinctTenantIds.Length == 0 || distinctTenantIds.Length > MaximumServiceabilityBatchSize)
        {
            throw new SharedValidationException(
                $"Between 1 and {MaximumServiceabilityBatchSize} distinct tenant ids are required.");
        }

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var found = await dbContext.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(tenant => distinctTenantIds.Contains(tenant.Id))
            .Select(tenant => new
            {
                tenant.Id,
                tenant.IsActive,
                tenant.IsDeleted,
                tenant.LifecycleStatus,
                tenant.LifecycleRevision
            })
            .ToDictionaryAsync(tenant => tenant.Id, cancellationToken);

        return new TenantServiceabilityBatchResult
        {
            Tenants = distinctTenantIds.Select(tenantId =>
            {
                if (!found.TryGetValue(tenantId, out var tenant))
                {
                    return new TenantServiceabilityDto
                    {
                        TenantId = tenantId,
                        Exists = false,
                        IsServiceable = false
                    };
                }

                var effectiveStatus = !tenant.IsActive && tenant.LifecycleStatus == TenantLifecycleStatus.Active
                    ? TenantLifecycleStatus.Suspended
                    : tenant.LifecycleStatus;
                return new TenantServiceabilityDto
                {
                    TenantId = tenantId,
                    Exists = true,
                    IsServiceable = !tenant.IsDeleted &&
                        tenant.IsActive &&
                        effectiveStatus == TenantLifecycleStatus.Active,
                    LifecycleStatus = effectiveStatus,
                    LifecycleRevision = tenant.LifecycleRevision
                };
            }).ToList()
        };
    }

    private static TenantConfigurationDto MapConfiguration(Guid id, string name, bool isActive)
    {
        return new TenantConfigurationDto
        {
            Id = id,
            Name = name,
            IsActive = isActive,
            ConnectionStrings = new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }
}
