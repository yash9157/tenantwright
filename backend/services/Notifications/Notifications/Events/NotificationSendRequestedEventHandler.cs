using Microsoft.Extensions.Logging;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Domain.Shared.Constants;
using Shared;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Events;

[ExposeServices(typeof(ITenantWrightMessageHandler<NotificationSendRequestedTenantEto>),
    typeof(ITenantWrightMessageHandler<NotificationSendRequestedHostEto>), IncludeSelf = true)]
public sealed class NotificationSendRequestedEventHandler(
    INotificationDispatchScheduler scheduler,
    ICurrentTenant currentTenant,
    ILogger<NotificationSendRequestedEventHandler> logger)
    : ITenantWrightMessageHandler<NotificationSendRequestedTenantEto>,
      ITenantWrightMessageHandler<NotificationSendRequestedHostEto>, ITransientDependency
{
    public async Task HandleAsync(NotificationSendRequestedTenantEto message, CancellationToken cancellationToken = default)
    {
        if (message.TenantId is null || message.TenantId == Guid.Empty || message.TenantId != currentTenant.Id)
            throw new SharedForbiddenException(NotificationErrorCodes.DispatchScopeMismatch, "Notification command scope must match the restored tenant.");
        var result = await scheduler.ScheduleAsync(message, cancellationToken);
        LogAdmission(result);
    }

    public async Task HandleAsync(NotificationSendRequestedHostEto message, CancellationToken cancellationToken = default)
    {
        if (currentTenant.Id.HasValue)
            throw new SharedForbiddenException(NotificationErrorCodes.DispatchScopeMismatch, "Host notification commands require host scope.");
        var result = await scheduler.ScheduleAsync(message, cancellationToken);
        LogAdmission(result);
    }

    private void LogAdmission(NotificationDispatchAdmissionDto admission) =>
        logger.LogDebug("Notification intent admitted. DispatchId={DispatchId}, RequestId={RequestId}, Duplicate={Duplicate}.",
            admission.DispatchId, admission.RequestId, admission.IsDuplicate);
}