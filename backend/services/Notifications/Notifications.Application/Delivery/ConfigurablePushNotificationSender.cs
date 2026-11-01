using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Domain.Devices;
using Notifications.Domain.Shared.Enums;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Notifications.Application.Contracts.ProviderConfig.Security;
using System.Security.Cryptography;
using System.Text;

namespace Notifications.Application.Delivery;

public sealed class ConfigurablePushNotificationSender : IPushNotificationSender, ITransientDependency
{
    private readonly IOptions<NotificationsChannelProviderModeOptions> _options;
    private readonly ICurrentTenant _currentTenant;
    private readonly INotificationProviderConfigResolver _resolver;
    private readonly IEnumerable<IPushProviderAdapter> _adapters;
    private readonly IRepository<NotificationDeviceToken, Guid> _deviceTokenRepository;
    private readonly INotificationsProviderSecretProtector _protector;

    public ConfigurablePushNotificationSender(
        IOptions<NotificationsChannelProviderModeOptions> options,
        ICurrentTenant currentTenant,
        INotificationProviderConfigResolver resolver,
        IEnumerable<IPushProviderAdapter> adapters,
        IRepository<NotificationDeviceToken, Guid> deviceTokenRepository,
        INotificationsProviderSecretProtector protector)
    {
        _options = options;
        _currentTenant = currentTenant;
        _resolver = resolver;
        _adapters = adapters;
        _deviceTokenRepository = deviceTokenRepository;
        _protector = protector;
    }

    public async Task<PushSendOutcome> SendAsync(
        string deviceToken,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        var mode = NotificationProviderModeParser.Parse(_options.Value.Push);

        switch (mode)
        {
            case NotificationProviderMode.MockSuccess:
                return new PushSendOutcome { ProviderMessageId = $"mock-push-{Guid.CreateVersion7():N}" };

            case NotificationProviderMode.Live:
                return await SendLiveAsync(deviceToken, title, body, cancellationToken);

            default:
                throw new InvalidOperationException(
                    "Push provider is disabled. Set Notifications:Providers:Push=MockSuccess|Live or configure a real provider.");
        }
    }

    private async Task<PushSendOutcome> SendLiveAsync(string deviceToken, string title, string body, CancellationToken cancellationToken)
    {
        var tenantId = _currentTenant.Id;

        var deviceTokens = await _deviceTokenRepository.GetQueryableAsync();
        var deviceTokenHash = Hash(deviceToken);
        var registeredDevice = await deviceTokens
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.DeviceTokenHash == deviceTokenHash && x.IsActive, cancellationToken);
        if (registeredDevice == null)
        {
            // A short-lived compatibility path for registrations created before encrypted storage.
            registeredDevice = await deviceTokens.FirstOrDefaultAsync(
                x => x.TenantId == tenantId && x.DeviceTokenHash == null && x.DeviceToken == deviceToken && x.IsActive,
                cancellationToken);
            if (registeredDevice != null)
            {
                registeredDevice.ProtectToken(
                    _protector.Protect(tenantId, NotificationChannel.Push, registeredDevice.Id, deviceToken),
                    deviceTokenHash);
                await _deviceTokenRepository.UpdateAsync(registeredDevice, autoSave: true, cancellationToken: cancellationToken);
            }
        }
        if (registeredDevice == null)
        {
            throw new InvalidOperationException("push_device_token_not_registered");
        }
        var decryptedDeviceToken = _protector.Unprotect(tenantId, NotificationChannel.Push, registeredDevice.Id, registeredDevice.DeviceToken);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(decryptedDeviceToken), Encoding.UTF8.GetBytes(deviceToken)))
            throw new InvalidOperationException("push_device_token_not_registered");

        var resolved = await _resolver.ResolveAsync(tenantId, NotificationChannel.Push, cancellationToken)
            ?? throw new InvalidOperationException(
                "No active push provider configuration is available. Configure a Super Admin default or tenant override.");

        var adapter = _adapters.FirstOrDefault(
            x => string.Equals(x.ProviderName, resolved.ProviderType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No push provider adapter is registered for provider '{resolved.ProviderType}'.");

        var result = await adapter.SendAsync(decryptedDeviceToken, title, body, resolved, cancellationToken);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode is "apns_bad_device_token" or "webpush_subscription_gone")
            {
                registeredDevice.Deactivate();
                await _deviceTokenRepository.UpdateAsync(registeredDevice, autoSave: true, cancellationToken: cancellationToken);
            }
            throw new InvalidOperationException(result.ErrorCode ?? "push_provider_failed");
        }

        return new PushSendOutcome { ProviderMessageId = result.ProviderMessageId, ProviderConfigId = resolved.ConfigId };
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
}
