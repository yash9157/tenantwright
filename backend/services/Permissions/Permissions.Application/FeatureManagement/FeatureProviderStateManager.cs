using Microsoft.EntityFrameworkCore;
using Permissions.Domain.FeatureManagement;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.FeatureManagement;

/// <summary>
/// Resolves the single durable concurrency row shared by every mutation of one feature provider.
/// The caller owns the ambient transaction and advances the returned tracked entity with its values.
/// </summary>
public sealed class FeatureProviderStateManager(
    IRepository<FeatureProviderState, Guid> stateRepository) : ITransientDependency
{
    public async Task<FeatureProviderState?> FindAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        bool asNoTracking = false,
        CancellationToken cancellationToken = default)
    {
        var query = await stateRepository.GetQueryableAsync();
        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(
            state => state.TenantId == tenantId &&
                     state.ProviderName == providerName &&
                     state.ProviderKey == providerKey,
            cancellationToken);
    }

    public async Task<FeatureProviderState> CreateAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        var state = new FeatureProviderState(Guid.CreateVersion7(), tenantId, providerName, providerKey);
        return await stateRepository.InsertAsync(state, autoSave: false, cancellationToken: cancellationToken);
    }
}
