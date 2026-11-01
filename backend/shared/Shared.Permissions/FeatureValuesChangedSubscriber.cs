using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.Permissions.Caching;
using Shared.Permissions.Client;

namespace Shared.Permissions;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<FeatureValuesChangedTenantEto>),
    typeof(ITenantWrightMessageHandler<FeatureValuesChangedHostEto>),
    IncludeSelf = true)]
public class FeatureValuesChangedEventHandler
    : ITenantWrightMessageHandler<FeatureValuesChangedTenantEto>,
        ITenantWrightMessageHandler<FeatureValuesChangedHostEto>,
        ITransientDependency
{
    private readonly PermissionProviderStampHybridCache _stampCache;
    private readonly TenantFeatureValueHybridCache _featureCache;
    private readonly Microsoft.Extensions.Options.IOptions<SharedPermissionClientOptions> _options;

    public FeatureValuesChangedEventHandler(
        PermissionProviderStampHybridCache stampCache,
        TenantFeatureValueHybridCache featureCache,
        Microsoft.Extensions.Options.IOptions<SharedPermissionClientOptions> options)
    {
        _stampCache = stampCache;
        _featureCache = featureCache;
        _options = options;
    }

    public Task HandleAsync(
        FeatureValuesChangedTenantEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (!eventData.TenantId.HasValue || eventData.TenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant feature-value events require a non-empty tenant id.");
        }

        return HandleAsync(eventData.FeatureNames, eventData.TenantId.Value, cancellationToken);
    }

    public Task HandleAsync(
        FeatureValuesChangedHostEto eventData,
        CancellationToken cancellationToken = default)
        => HandleAsync(eventData.FeatureNames, tenantId: null, cancellationToken);

    private async Task HandleAsync(
        IReadOnlyCollection<string> featureNames,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        var tenantPart = tenantId?.ToString("D") ?? "host";
        var stampKey = PermissionCacheKeys.BuildProviderKey(tenantId, "Feature", tenantPart);

        // The Permissions service publishes the committed durable provider stamp separately through
        // PermissionGrantsChangedEto. Removing here is safe for either event order and prevents an
        // event timestamp from replacing the database-backed feature-provider version.
        await _stampCache.RemoveAsync(
            stampKey,
            considerUow: false,
            token: cancellationToken);

        foreach (var featureName in featureNames)
        {
            if (!string.IsNullOrWhiteSpace(featureName))
            {
                await _featureCache.RemoveAsync(
                    PermissionCacheKeys.BuildTenantFeatureKey(tenantId, featureName.Trim()),
                    considerUow: false,
                    token: cancellationToken);
            }
        }
    }
}
