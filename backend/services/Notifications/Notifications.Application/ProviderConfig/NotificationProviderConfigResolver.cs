using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Domain.Notifications;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Enums;
using Shared.Caching.Abstractions;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;

namespace Notifications.Application.ProviderConfig;

/// <summary>
/// Resolves the active provider configuration for a channel send: a verified, active tenant
/// override first, otherwise the host/Super Admin default, otherwise null. Never falls back
/// across unrelated tenants. Cached per (tenant scope, channel) for 2 minutes, matching the
/// existing email default-config cache TTL.
/// </summary>
[DisableConventionalRegistration]
internal sealed class NotificationProviderConfigResolver : INotificationProviderConfigResolver, ITransientDependency
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(2)
    };

    private readonly IRepository<NotificationProviderConfig, Guid> _repository;
    private readonly IRepository<NotificationDelivery, long> _deliveryRepository;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly INotificationsProviderSecretProtector _protector;
    private readonly IHybridCache<NotificationProviderConfigCacheItem, string> _cache;

    public NotificationProviderConfigResolver(
        IRepository<NotificationProviderConfig, Guid> repository,
        IRepository<NotificationDelivery, long> deliveryRepository,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        INotificationsProviderSecretProtector protector,
        IHybridCache<NotificationProviderConfigCacheItem, string> cache)
    {
        _repository = repository;
        _deliveryRepository = deliveryRepository;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _protector = protector;
        _cache = cache;
    }

    public async Task<ResolvedNotificationProviderConfig?> ResolveAsync(
        Guid? tenantId,
        NotificationChannel channel,
        CancellationToken cancellationToken = default)
    {
        if (tenantId.HasValue)
        {
            var tenantItem = await LookupAsync(tenantId, channel, cancellationToken);
            if (tenantItem.Found)
            {
                return Materialize(tenantItem, channel);
            }
        }

        var hostItem = await LookupAsync(null, channel, cancellationToken);
        return hostItem.Found ? Materialize(hostItem, channel) : null;
    }

    public async Task<ResolvedNotificationProviderConfig?> ResolveByProviderMessageIdAsync(
        string providerMessageId,
        NotificationChannel channel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId))
        {
            return null;
        }

        var deliveries = await _deliveryRepository.GetQueryableAsync();
        var configId = await deliveries
            .IgnoreQueryFilters()
            .Where(x => x.ProviderMessageId == providerMessageId && x.ProviderConfigId != null && x.Notification.Channel == channel)
            .Select(x => x.ProviderConfigId)
            .FirstOrDefaultAsync(cancellationToken);

        if (configId == null)
        {
            return null;
        }

        var configs = await _repository.GetQueryableAsync();
        var entity = await configs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == configId.Value, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        return new ResolvedNotificationProviderConfig
        {
            ConfigId = entity.Id,
            ConfigTenantId = entity.TenantId,
            Channel = entity.Channel,
            ProviderType = entity.ProviderType,
            SettingsJson = entity.SettingsJson,
            Secret = string.IsNullOrWhiteSpace(entity.SecretEnvelope)
                ? null
                : _protector.Unprotect(entity.TenantId, entity.Channel, entity.Id, entity.SecretEnvelope)
        };
    }

    public async Task<ResolvedNotificationProviderConfig?> ResolveByConfigIdAsync(Guid configId, NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        if (configId == Guid.Empty) return null;
        var configs = await _repository.GetQueryableAsync();
        var entity = await configs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == configId && x.Channel == channel && x.IsActive, cancellationToken);
        if (entity == null) return null;
        return new ResolvedNotificationProviderConfig
        {
            ConfigId = entity.Id,
            ConfigTenantId = entity.TenantId,
            Channel = entity.Channel,
            ProviderType = entity.ProviderType,
            SettingsJson = entity.SettingsJson,
            Secret = string.IsNullOrWhiteSpace(entity.SecretEnvelope) ? null : _protector.Unprotect(entity.TenantId, entity.Channel, entity.Id, entity.SecretEnvelope)
        };
    }

    private async Task<NotificationProviderConfigCacheItem> LookupAsync(
        Guid? scopeTenantId,
        NotificationChannel channel,
        CancellationToken cancellationToken)
    {
        var cacheKey = NotificationProviderConfigCacheKeys.Default(scopeTenantId, channel);
        var item = await _cache.GetOrCreateAsync(
            cacheKey,
            async () => await _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
                async operationCancellationToken =>
                {
                    var queryable = await _repository.GetQueryableAsync();
                    var entity = await queryable
                        .Where(x => x.Channel == channel && x.TenantId == scopeTenantId && x.IsDefault && x.IsActive)
                        .AsNoTracking()
                        .FirstOrDefaultAsync(operationCancellationToken);

                    if (entity == null)
                    {
                        return new NotificationProviderConfigCacheItem { Found = false };
                    }

                    return new NotificationProviderConfigCacheItem
                    {
                        Found = true,
                        ConfigId = entity.Id,
                        ConfigTenantId = entity.TenantId,
                        ProviderType = entity.ProviderType,
                        SettingsJson = entity.SettingsJson,
                        SecretEnvelope = entity.SecretEnvelope
                    };
                },
                cancellationToken),
            optionsFactory: () => CacheOptions,
            considerUow: false,
            token: cancellationToken);

        return item ?? new NotificationProviderConfigCacheItem { Found = false };
    }

    private ResolvedNotificationProviderConfig Materialize(NotificationProviderConfigCacheItem item, NotificationChannel channel)
    {
        var secret = string.IsNullOrWhiteSpace(item.SecretEnvelope)
            ? null
            : _protector.Unprotect(item.ConfigTenantId, channel, item.ConfigId, item.SecretEnvelope);

        return new ResolvedNotificationProviderConfig
        {
            ConfigId = item.ConfigId,
            ConfigTenantId = item.ConfigTenantId,
            Channel = channel,
            ProviderType = item.ProviderType,
            SettingsJson = item.SettingsJson,
            Secret = secret
        };
    }
}
