using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Application.ProviderConfig.Security;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared;
using Shared.Caching.Abstractions;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Notifications.Application.ProviderConfig;

/// <summary>
/// Shared CRUD/ownership/default-selection/cache-invalidation logic for
/// <see cref="NotificationProviderConfig"/>, parameterized by channel. Reused by every
/// per-channel config app service (Email/Sms/Push/WhatsApp) instead of duplicating the
/// tenant-scoping and default-selection rules four times. Implementation detail with one
/// implementation, so no public interface (TenantWright coding standard).
/// </summary>
internal sealed class NotificationProviderConfigStore : ITransientDependency
{
    private readonly IRepository<NotificationProviderConfig, Guid> _repository;
    private readonly ICurrentTenant _currentTenant;
    private readonly INotificationsProviderSecretProtector _protector;
    private readonly IHybridCache<NotificationProviderConfigCacheItem, string> _cache;
    private readonly IOptions<NotificationsProviderSecretProtectionOptions>? _protectionOptions;

    public NotificationProviderConfigStore(
        IRepository<NotificationProviderConfig, Guid> repository,
        ICurrentTenant currentTenant,
        INotificationsProviderSecretProtector protector,
        IHybridCache<NotificationProviderConfigCacheItem, string> cache,
        IOptions<NotificationsProviderSecretProtectionOptions>? protectionOptions = null)
    {
        _repository = repository;
        _currentTenant = currentTenant;
        _protector = protector;
        _cache = cache;
        _protectionOptions = protectionOptions;
    }

    public async Task<List<NotificationProviderConfig>> GetListAsync(NotificationChannel channel, Guid? tenantId)
    {
        tenantId = ResolveTenantScope(tenantId);
        var queryable = await _repository.GetQueryableAsync();
        return await queryable
            .Where(x => x.Channel == channel && x.TenantId == tenantId)
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .AsNoTracking()
            .ToListAsync();
    }

    public Task<List<NotificationProviderConfig>> GetCurrentTenantListAsync(NotificationChannel channel)
    {
        return GetListAsync(channel, RequireCurrentTenantId());
    }

    public async Task<NotificationProviderConfig> GetAsync(NotificationChannel channel, Guid id)
    {
        var entity = await _repository.GetAsync(id);
        EnsureOwnership(entity);
        EnsureChannel(entity, channel);
        return entity;
    }

    public async Task<NotificationProviderConfig> CreateAsync(
        NotificationChannel channel,
        Guid? tenantId,
        string providerType,
        string name,
        bool isActive,
        bool isDefault,
        int priority,
        string settingsJson,
        string? complianceContact,
        string? secretPlaintext)
    {
        var scopedTenantId = ResolveTenantScope(tenantId);
        await EnsureNameAvailableAsync(channel, scopedTenantId, name, excludingId: null);

        if (isDefault)
        {
            await ClearExistingDefaultAsync(channel, scopedTenantId);
        }

        var entity = new NotificationProviderConfig(
            id: Guid.CreateVersion7(),
            tenantId: scopedTenantId,
            channel: channel,
            providerType: providerType,
            name: name,
            isActive: isActive,
            isDefault: isDefault,
            priority: priority,
            settingsJson: settingsJson,
            complianceContact: complianceContact);

        if (!string.IsNullOrWhiteSpace(secretPlaintext))
        {
            ApplySecret(entity, secretPlaintext);
        }

        await _repository.InsertAsync(entity, autoSave: true);

        if (entity.IsDefault)
        {
            await InvalidateDefaultCacheAsync(channel, scopedTenantId);
        }

        return entity;
    }

    public async Task<NotificationProviderConfig> UpdateAsync(
        NotificationChannel channel,
        Guid id,
        string providerType,
        string name,
        bool isActive,
        int priority,
        string settingsJson,
        string? complianceContact,
        string? secretPlaintext)
    {
        var entity = await _repository.GetAsync(id);
        EnsureOwnership(entity);
        EnsureChannel(entity, channel);
        await EnsureNameAvailableAsync(channel, entity.TenantId, name, excludingId: entity.Id);

        entity.UpdateSettings(providerType, name, isActive, priority, settingsJson, complianceContact);

        if (!string.IsNullOrWhiteSpace(secretPlaintext))
        {
            ApplySecret(entity, secretPlaintext);
        }

        await _repository.UpdateAsync(entity, autoSave: true);

        if (entity.IsDefault)
        {
            await InvalidateDefaultCacheAsync(channel, entity.TenantId);
        }

        return entity;
    }

    public async Task DeleteAsync(NotificationChannel channel, Guid id)
    {
        var entity = await _repository.GetAsync(id);
        EnsureOwnership(entity);
        EnsureChannel(entity, channel);

        if (entity.IsDefault)
        {
            var queryable = await _repository.GetQueryableAsync();
            var otherConfigExists = await queryable
                .Where(x => x.Channel == channel && x.TenantId == entity.TenantId && x.Id != entity.Id)
                .AnyAsync();

            if (otherConfigExists)
            {
                throw new SharedConflictException(
                    NotificationErrorCodes.DefaultProviderDeleteBlocked,
                    "Cannot delete the default provider configuration while other configurations exist for this channel. Set a different configuration as default first.");
            }

            entity.ClearDefault();
        }

        await _repository.DeleteAsync(entity, autoSave: true);
        await InvalidateDefaultCacheAsync(channel, entity.TenantId);
    }

    public async Task<NotificationProviderConfig> SetDefaultAsync(NotificationChannel channel, Guid id)
    {
        var entity = await _repository.GetAsync(id);
        EnsureOwnership(entity);
        EnsureChannel(entity, channel);

        if (entity.IsDefault)
        {
            return entity;
        }

        await ClearExistingDefaultAsync(channel, entity.TenantId);

        entity.MarkAsDefault();
        await _repository.UpdateAsync(entity, autoSave: true);
        await InvalidateDefaultCacheAsync(channel, entity.TenantId);

        return entity;
    }

    public async Task RecordTestResultAsync(NotificationProviderConfig entity, DateTime testedAtUtc, string result)
    {
        entity.RecordTestResult(testedAtUtc, result);
        await _repository.UpdateAsync(entity, autoSave: true);
    }

    public string? DecryptSecret(NotificationProviderConfig entity)
    {
        if (string.IsNullOrWhiteSpace(entity.SecretEnvelope))
        {
            return null;
        }

        return _protector.Unprotect(entity.TenantId, entity.Channel, entity.Id, entity.SecretEnvelope);
    }

    public Guid RequireCurrentTenantId()
    {
        return _currentTenant.Id
            ?? throw new UnauthorizedAccessException("Tenant provider configuration requires an active tenant context.");
    }

    public Guid? ResolveTenantScope(Guid? requestedTenantId)
    {
        if (!_currentTenant.Id.HasValue)
        {
            if (requestedTenantId.HasValue)
            {
                throw new UnauthorizedAccessException("Host context cannot manage tenant provider configuration through this endpoint.");
            }

            return null;
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != _currentTenant.Id.Value)
        {
            throw new UnauthorizedAccessException("Provider configuration must match the authenticated tenant.");
        }

        return _currentTenant.Id.Value;
    }

    public void EnsureOwnership(NotificationProviderConfig entity)
    {
        if (entity.TenantId != _currentTenant.Id)
        {
            throw new UnauthorizedAccessException("Provider configuration must match the authenticated tenant.");
        }
    }

    private static void EnsureChannel(NotificationProviderConfig entity, NotificationChannel channel)
    {
        if (entity.Channel != channel)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.ProviderConfigChannelMismatch,
                $"Provider configuration '{entity.Id}' does not belong to channel '{channel}'.");
        }
    }

    private void ApplySecret(NotificationProviderConfig entity, string secretPlaintext)
    {
        var envelope = _protector.Protect(entity.TenantId, entity.Channel, entity.Id, secretPlaintext);
        var fingerprint = _protector.Fingerprint(secretPlaintext);
        entity.SetSecret(
            envelope,
            fingerprint,
            _protectionOptions?.Value.CurrentKeyId);
    }

    private async Task EnsureNameAvailableAsync(NotificationChannel channel, Guid? tenantId, string name, Guid? excludingId)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        var queryable = await _repository.GetQueryableAsync();
        var query = queryable.Where(x => x.Channel == channel && x.TenantId == tenantId && x.Name == normalizedName);
        if (excludingId.HasValue)
        {
            query = query.Where(x => x.Id != excludingId.Value);
        }

        if (await query.AnyAsync())
        {
            throw new SharedConflictException(
                NotificationErrorCodes.ProviderConfigNameExists,
                $"A configuration named '{normalizedName}' already exists for this context.");
        }
    }

    private async Task ClearExistingDefaultAsync(NotificationChannel channel, Guid? tenantId)
    {
        var queryable = await _repository.GetQueryableAsync();
        var currentDefault = await queryable
            .Where(x => x.Channel == channel && x.TenantId == tenantId && x.IsDefault)
            .FirstOrDefaultAsync();

        if (currentDefault != null)
        {
            currentDefault.ClearDefault();
            await _repository.UpdateAsync(currentDefault, autoSave: true);
        }
    }

    private Task InvalidateDefaultCacheAsync(NotificationChannel channel, Guid? tenantId)
    {
        return _cache.RemoveAsync(NotificationProviderConfigCacheKeys.Default(tenantId, channel), considerUow: true);
    }
}
