using Notifications.Application.Contracts.Delivery;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Notifications.Application.Delivery;

public sealed class ConfigurableSmsNotificationSender : ISmsNotificationSender, ITransientDependency
{
    private readonly IOptions<NotificationsChannelProviderModeOptions> _options;
    private readonly ICurrentTenant _currentTenant;
    private readonly INotificationProviderConfigResolver _resolver;
    private readonly IEnumerable<ISmsProviderAdapter> _adapters;

    public ConfigurableSmsNotificationSender(
        IOptions<NotificationsChannelProviderModeOptions> options,
        ICurrentTenant currentTenant,
        INotificationProviderConfigResolver resolver,
        IEnumerable<ISmsProviderAdapter> adapters)
    {
        _options = options;
        _currentTenant = currentTenant;
        _resolver = resolver;
        _adapters = adapters;
    }

    public async Task<NotificationProviderSendResult> SendAsync(
        string phoneNumber,
        string message,
        CancellationToken cancellationToken = default)
    {
        var mode = NotificationProviderModeParser.Parse(_options.Value.Sms);

        switch (mode)
        {
            case NotificationProviderMode.MockSuccess:
                return NotificationProviderSendResult.Success($"mock-sms-{Guid.CreateVersion7():N}");

            case NotificationProviderMode.Live:
                return await SendLiveAsync(phoneNumber, message, cancellationToken);

            default:
                throw new NotificationChannelUnavailableException("provider_configuration_unavailable",
                    "Sms provider is disabled. Set Notifications:Providers:Sms=MockSuccess|Live or configure a real provider.");
        }
    }

    private async Task<NotificationProviderSendResult> SendLiveAsync(string phoneNumber, string message, CancellationToken cancellationToken)
    {
        var resolved = await _resolver.ResolveAsync(_currentTenant.Id, NotificationChannel.Sms, cancellationToken)
            ?? throw new NotificationChannelUnavailableException("provider_configuration_unavailable",
                "No active SMS provider configuration is available. Configure a Super Admin default or tenant override.");

        var adapter = _adapters.FirstOrDefault(
            x => string.Equals(x.ProviderName, resolved.ProviderType, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotificationChannelUnavailableException("provider_configuration_unavailable",
                $"No SMS provider adapter is registered for provider '{resolved.ProviderType}'.");

        var result = await adapter.SendAsync(phoneNumber, message, resolved, cancellationToken);
        if (!result.IsSuccess)
        {
            // Surface the failure so the delivery worker records it and applies retry/dead-letter policy.
            return result.WithConfiguration(resolved.ConfigId);
        }

        return result.WithConfiguration(resolved.ConfigId);
    }
}
