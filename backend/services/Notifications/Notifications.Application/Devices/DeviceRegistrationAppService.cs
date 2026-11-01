using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Devices;
using Notifications.Application.Contracts.Channels;
using Notifications.Domain.Devices;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Domain.Shared.Enums;

using Shared.AuditLogs.Auditing;
namespace Notifications.Application.Devices;

[DisableConventionalRegistration]
internal sealed class DeviceRegistrationAppService : IDeviceRegistrationAppService, ITransientDependency
{
    private readonly IRepository<NotificationDeviceToken, Guid> _repository;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;
    private readonly INotificationsProviderSecretProtector _protector;

    public DeviceRegistrationAppService(
        IRepository<NotificationDeviceToken, Guid> repository,
        ICurrentTenant currentTenant,
        ICurrentPrincipalAccessor currentPrincipalAccessor,
        INotificationsProviderSecretProtector protector)
    {
        _repository = repository;
        _currentTenant = currentTenant;
        _currentPrincipalAccessor = currentPrincipalAccessor;
        _protector = protector;
    }

    [Audited]
    public async Task<NotificationDeviceTokenDto> RegisterAsync(RegisterDeviceTokenDto input)
    {
        var deviceToken = ResolveRegistrationPayload(input, out var lookupValue);
        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            throw new ArgumentException("Device token is required.", nameof(input));
        }

        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();
        var nowUtc = DateTime.UtcNow;
        var deviceTokenHash = Hash(lookupValue);

        var queryable = await _repository.GetQueryableAsync();
        var existing = await queryable
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.DeviceTokenHash == deviceTokenHash);

        if (existing != null)
        {
            existing.ProtectToken(_protector.Protect(tenantId, NotificationChannel.Push, existing.Id, deviceToken), deviceTokenHash);
            existing.RefreshRegistration(userId, input.Platform, input.AppIdentifier, nowUtc);
            await _repository.UpdateAsync(existing, autoSave: true);
            return Map(existing);
        }

        // Registrations created before token-at-rest protection have no hash. Upgrade only after
        // the caller proves possession of the original raw token.
        var legacy = await queryable.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.DeviceTokenHash == null && x.DeviceToken == lookupValue);
        if (legacy != null)
        {
            legacy.ProtectToken(_protector.Protect(tenantId, NotificationChannel.Push, legacy.Id, deviceToken), deviceTokenHash);
            legacy.RefreshRegistration(userId, input.Platform, input.AppIdentifier, nowUtc);
            await _repository.UpdateAsync(legacy, autoSave: true);
            return Map(legacy);
        }

        var id = Guid.CreateVersion7();
        var entity = new NotificationDeviceToken(
            id: id,
            tenantId: tenantId,
            userId: userId,
            deviceToken: _protector.Protect(tenantId, NotificationChannel.Push, id, deviceToken),
            deviceTokenHash: deviceTokenHash,
            platform: input.Platform,
            appIdentifier: input.AppIdentifier,
            nowUtc: nowUtc);

        await _repository.InsertAsync(entity, autoSave: true);

        return Map(entity);
    }

    [Audited]
    public async Task UnregisterAsync(Guid id)
    {
        if (id == Guid.Empty)
        {
            return;
        }

        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();

        var queryable = await _repository.GetQueryableAsync();
        var entity = await queryable
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.UserId == userId);

        if (entity == null)
        {
            return;
        }

        entity.Deactivate();
        await _repository.UpdateAsync(entity, autoSave: true);
    }

    public async Task<List<NotificationDeviceTokenDto>> GetMyDevicesAsync()
    {
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();

        var queryable = await _repository.GetQueryableAsync();
        var entities = await queryable
            .Where(x => x.TenantId == tenantId && x.UserId == userId && x.IsActive)
            .AsNoTracking()
            .ToListAsync();

        return entities.Select(Map).ToList();
    }

    private string GetCurrentUserId()
    {
        var principal = _currentPrincipalAccessor.Principal;
        var userId = principal.FindFirstValue(SharedClaimTypes.Subject)
            ?? principal.FindFirstValue(SharedClaimTypes.UserId);

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException("Missing user identity.");
        }

        return userId.Trim();
    }

    private static NotificationDeviceTokenDto Map(NotificationDeviceToken entity)
    {
        return new NotificationDeviceTokenDto
        {
            Id = entity.Id,
            DeviceTokenMask = entity.DeviceTokenHash is { Length: >= 8 } hash ? $"…{hash[^8..]}" : "legacy",
            Platform = entity.Platform,
            AppIdentifier = entity.AppIdentifier,
            IsActive = entity.IsActive,
            LastSeenAtUtc = entity.LastSeenAtUtc
        };
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.Trim())));

    private static string ResolveRegistrationPayload(RegisterDeviceTokenDto input, out string lookupValue)
    {
        if (input.Platform != NotificationDevicePlatform.Web)
        {
            lookupValue = input.DeviceToken?.Trim() ?? string.Empty;
            return lookupValue;
        }

        var endpoint = input.WebPushEndpoint?.Trim() ?? string.Empty;
        var p256dh = input.WebPushP256dh?.Trim() ?? string.Empty;
        var auth = input.WebPushAuth?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(p256dh) || string.IsNullOrWhiteSpace(auth))
        {
            throw new ArgumentException("A HTTPS Web Push endpoint with p256dh and auth keys is required.", nameof(input));
        }

        lookupValue = endpoint;
        return JsonSerializer.Serialize(new WebPushSubscriptionPayload { Endpoint = endpoint, P256dh = p256dh, Auth = auth });
    }
}
