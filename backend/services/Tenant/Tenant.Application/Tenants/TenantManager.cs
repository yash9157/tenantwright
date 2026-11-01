using Shared;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Tenant.Application.Contracts.Tenants;
using Tenant.Domain.Shared.Constants;
using TenantEntity = Tenant.Domain.Tenants.Tenant;
using Microsoft.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

public class TenantManager : ITransientDependency
{
    private readonly IRepository<TenantEntity, Guid> _tenantRepository;

    public TenantManager(IRepository<TenantEntity, Guid> tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    public async Task<TenantEntity> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        name = NormalizeNameInput(name);
        var normalizedName = NormalizeName(name);

        if (await NormalizedNameExistsAsync(normalizedName, cancellationToken: cancellationToken))
        {
            throw new TenantNameAlreadyExistsException(name);
        }

        return new TenantEntity(Guid.CreateVersion7(), name);
    }

    public async Task ChangeNameAsync(TenantEntity tenant, string newName, CancellationToken cancellationToken = default)
    {
        newName = NormalizeNameInput(newName);
        var normalizedName = NormalizeName(newName);

        if (await NormalizedNameExistsAsync(normalizedName, ignoreTenantId: tenant.Id, cancellationToken: cancellationToken))
        {
            throw new TenantNameAlreadyExistsException(newName);
        }

        tenant.SetName(newName);
    }

    private static string NormalizeNameInput(string name)
    {
        return Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxNameLength).Trim();
    }

    private static string NormalizeName(string name)
    {
        return name.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Whether a non-deleted tenant already holds the given normalized name. The
    /// underlying repository query applies the soft-delete filter, so only active
    /// (non-deleted) tenants count. Used by restore to block bringing back a name
    /// that an active tenant now owns.
    /// </summary>
    public Task<bool> IsNameTakenAsync(
        string normalizedName,
        Guid? ignoreTenantId = null,
        CancellationToken cancellationToken = default)
    {
        return NormalizedNameExistsAsync(normalizedName, ignoreTenantId, cancellationToken);
    }

    private async Task<bool> NormalizedNameExistsAsync(
        string normalizedName,
        Guid? ignoreTenantId = null,
        CancellationToken cancellationToken = default)
    {
        var query = await _tenantRepository.GetQueryableAsync();
        if (ignoreTenantId.HasValue)
        {
            query = query.Where(x => x.Id != ignoreTenantId.Value);
        }

        return await query.AnyAsync(x => x.NormalizedName == normalizedName, cancellationToken);
    }
}
