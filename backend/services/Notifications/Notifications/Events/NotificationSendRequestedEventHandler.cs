using Microsoft.Extensions.Logging;
using Notifications.Application.Contracts.Delivery;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Events;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<NotificationSendRequestedTenantEto>),
    typeof(ITenantWrightMessageHandler<NotificationSendRequestedHostEto>),
    IncludeSelf = true)]
public sealed class NotificationSendRequestedEventHandler
    : ITenantWrightMessageHandler<NotificationSendRequestedTenantEto>,
        ITenantWrightMessageHandler<NotificationSendRequestedHostEto>,
        ITransientDependency
{
    private readonly INotificationDeliveryScheduler _scheduler;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<NotificationSendRequestedEventHandler> _logger;

    public NotificationSendRequestedEventHandler(
        INotificationDeliveryScheduler scheduler,
        ICurrentTenant currentTenant,
        ILogger<NotificationSendRequestedEventHandler> logger)
    {
        _scheduler = scheduler;
        _currentTenant = currentTenant;
        _logger = logger;
    }

    public Task HandleAsync(
        NotificationSendRequestedTenantEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (!eventData.TenantId.HasValue || eventData.TenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant notification commands require a non-empty tenant id.");
        }

        return HandleTenantAsync(eventData, cancellationToken);
    }

    public Task HandleAsync(
        NotificationSendRequestedHostEto eventData,
        CancellationToken cancellationToken = default)
        => HandleHostAsync(eventData, cancellationToken);

    private async Task HandleTenantAsync(
        NotificationSendRequestedTenantEto eventData,
        CancellationToken cancellationToken)
    {
        if (_currentTenant.Id.HasValue && _currentTenant.Id.Value != eventData.TenantId)
        {
            throw new InvalidOperationException("Notification command tenant does not match the current tenant context.");
        }

        using var tenantScope = _currentTenant.Change(eventData.TenantId);
        NotificationScheduleResult result;
        try
        {
            result = eventData.ScheduledForUtc.HasValue
                ? await _scheduler.ScheduleAtAsync(
                    eventData,
                    eventData.ScheduledForUtc.Value,
                    cancellationToken)
                : await _scheduler.ScheduleAsync(eventData, cancellationToken);
        }
        catch (NotificationChannelDisabledByFeatureException ex)
        {
            LogFeatureDisabled(eventData, eventData.TenantId, ex);
            return;
        }

        LogOutcome(eventData, eventData.TenantId, result);
    }

    private async Task HandleHostAsync(
        NotificationSendRequestedHostEto eventData,
        CancellationToken cancellationToken)
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new InvalidOperationException("Host notification commands cannot run inside a tenant context.");
        }

        NotificationScheduleResult result;
        try
        {
            result = eventData.ScheduledForUtc.HasValue
                ? await _scheduler.ScheduleAtAsync(
                    eventData,
                    eventData.ScheduledForUtc.Value,
                    cancellationToken)
                : await _scheduler.ScheduleAsync(eventData, cancellationToken);
        }
        catch (NotificationChannelDisabledByFeatureException ex)
        {
            LogFeatureDisabled(eventData, tenantId: null, ex);
            return;
        }

        LogOutcome(eventData, tenantId: null, result);
    }

    private void LogFeatureDisabled(
        NotificationSendRequestedTenantEto eventData,
        Guid? tenantId,
        NotificationChannelDisabledByFeatureException exception)
        => LogFeatureDisabled(eventData.RequestId, tenantId, exception);

    private void LogFeatureDisabled(
        NotificationSendRequestedHostEto eventData,
        Guid? tenantId,
        NotificationChannelDisabledByFeatureException exception)
        => LogFeatureDisabled(eventData.RequestId, tenantId, exception);

    private void LogFeatureDisabled(
        Guid requestId,
        Guid? tenantId,
        NotificationChannelDisabledByFeatureException exception)
    {
        _logger.LogWarning(
            exception,
            "Notification request skipped because channel is disabled by feature. RequestId={RequestId}, TenantId={TenantId}, Channel={Channel}, Feature={Feature}",
            requestId,
            tenantId,
            exception.Channel,
            exception.FeatureName);
    }

    private void LogOutcome(
        NotificationSendRequestedTenantEto eventData,
        Guid? tenantId,
        NotificationScheduleResult result)
        => LogOutcome(
            eventData.PreferenceUserId,
            eventData.PreferenceKey,
            tenantId,
            result);

    private void LogOutcome(
        NotificationSendRequestedHostEto eventData,
        Guid? tenantId,
        NotificationScheduleResult result)
        => LogOutcome(
            eventData.PreferenceUserId,
            eventData.PreferenceKey,
            tenantId,
            result);

    private void LogOutcome(
        Guid? preferenceUserId,
        string? preferenceKey,
        Guid? tenantId,
        NotificationScheduleResult result)
    {
        if (result.IsDuplicate)
        {
            _logger.LogDebug(
                "Duplicate notification send request skipped. RequestId={RequestId}, NotificationId={NotificationId}",
                result.RequestId,
                result.NotificationId);
        }
        else if (result.SkippedByPreferences)
        {
            _logger.LogDebug(
                "Notification send request skipped by user preferences. RequestId={RequestId}, TenantId={TenantId}, PreferenceUserId={PreferenceUserId}, PreferenceKey={PreferenceKey}",
                result.RequestId,
                tenantId,
                preferenceUserId,
                preferenceKey);
        }
    }
}
