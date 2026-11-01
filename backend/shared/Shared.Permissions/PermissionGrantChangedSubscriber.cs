using Microsoft.Extensions.Caching.Hybrid;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.Permissions.Caching;
using Shared.Permissions.Client;

namespace Shared.Permissions;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>),
    typeof(ITenantWrightMessageHandler<PermissionGrantsChangedHostEto>),
    IncludeSelf = true)]
public class PermissionGrantChangedEventHandler
    : ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>,
        ITenantWrightMessageHandler<PermissionGrantsChangedHostEto>,
        ITransientDependency
{
    private static readonly HybridCacheEntryOptions StampCacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1)
    };

    private readonly PermissionProviderStampHybridCache _stampCache;
    private readonly Microsoft.Extensions.Options.IOptions<SharedPermissionClientOptions> _options;

    public PermissionGrantChangedEventHandler(
        PermissionProviderStampHybridCache stampCache,
        Microsoft.Extensions.Options.IOptions<SharedPermissionClientOptions> options)
    {
        _stampCache = stampCache;
        _options = options;
    }

    public Task HandleAsync(
        PermissionGrantsChangedTenantEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (!eventData.TenantId.HasValue || eventData.TenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant permission-grant events require a non-empty tenant id.");
        }

        return HandleAsync(
            eventData.ProviderName,
            eventData.ProviderKey,
            eventData.OccurredAtUtc,
            eventData.Stamp,
            eventData.TenantId.Value,
            cancellationToken);
    }

    public Task HandleAsync(
        PermissionGrantsChangedHostEto eventData,
        CancellationToken cancellationToken = default)
        => HandleAsync(
            eventData.ProviderName,
            eventData.ProviderKey,
            eventData.OccurredAtUtc,
            eventData.Stamp,
            tenantId: null,
            cancellationToken);

    private async Task HandleAsync(
        string providerName,
        string providerKey,
        DateTimeOffset occurredAtUtc,
        long stamp,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        if (stamp <= 0)
        {
            stamp = occurredAtUtc.UtcTicks;
        }

        var key = PermissionCacheKeys.BuildProviderKey(tenantId, providerName, providerKey);

        await _stampCache.SetAsync(
            key,
            new PermissionProviderStampCacheItem
            {
                Stamp = stamp,
                UpdatedAtUtc = occurredAtUtc
            },
            options: StampCacheOptions,
            considerUow: false,
            token: cancellationToken);
    }
}
