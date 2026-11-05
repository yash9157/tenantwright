using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Events;
using Shared.EventContracts;
using Xunit;

namespace Notifications.Tests;

public sealed class PermissionGrantApprovalNotificationHandlerTests
{
    [Fact]
    public async Task Requested_event_targets_direct_users_and_roles_once()
    {
        var scheduler = new CapturingScheduler();
        var currentTenant = new TestCurrentTenant(null);
        var notificationHandler = new NotificationSendRequestedEventHandler(
            scheduler, currentTenant, NullLogger<NotificationSendRequestedEventHandler>.Instance);
        var handler = new PermissionGrantApprovalRequestedEventHandler(
            notificationHandler,
            NullLogger<PermissionGrantApprovalRequestedEventHandler>.Instance);
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        using var scope = currentTenant.Change(tenantId);

        await handler.HandleAsync(new PermissionGrantApprovalRequestedEto
        {
            RequestId = Guid.NewGuid(),
            TenantId = tenantId,
            PermissionName = "Payments.Refunds.Approve",
            ProviderDisplayName = "Operations lead",
            RequesterDisplayName = "Asha Rao",
            ApproverUserIds = [userId, userId],
            ApproverRoleNames = ["HostAdmin", "HostAdmin"],
            OccurredAtUtc = DateTime.UtcNow
        });

        var notification = Assert.IsType<NotificationSendRequestedTenantEto>(scheduler.LastInput);
        Assert.Equal(tenantId, notification.TenantId);
        Assert.Equal(NotificationSendChannel.App, Assert.Single(notification.DeliveryPlan.Steps).Channel);
        Assert.Equal(2, notification.Recipients.Count);
        Assert.Equal("/platform/access/approvals", notification.ActionUrl);
    }

    [Fact]
    public async Task Decided_event_notifies_only_the_requester()
    {
        var scheduler = new CapturingScheduler();
        var currentTenant = new TestCurrentTenant(null);
        var notificationHandler = new NotificationSendRequestedEventHandler(
            scheduler, currentTenant, NullLogger<NotificationSendRequestedEventHandler>.Instance);
        var handler = new PermissionGrantApprovalDecidedEventHandler(notificationHandler);
        var requesterId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        using var scope = currentTenant.Change(tenantId);

        await handler.HandleAsync(new PermissionGrantApprovalDecidedEto
        {
            RequestId = Guid.NewGuid(),
            TenantId = tenantId,
            RequesterUserId = requesterId,
            PermissionName = "Payments.Refunds.Approve",
            ProviderDisplayName = "Operations lead",
            Decision = "Applied",
            DecisionReason = "Reviewed",
            DeciderDisplayName = "Host admin",
            OccurredAtUtc = DateTime.UtcNow
        });

        var notification = Assert.IsType<NotificationSendRequestedTenantEto>(scheduler.LastInput);
        Assert.Equal(tenantId, notification.TenantId);
        Assert.Equal(requesterId.ToString("D"), Assert.Single(Assert.Single(notification.Recipients).Destinations).RecipientValue);
    }

    private sealed class CapturingScheduler : INotificationDispatchScheduler
    {
        public object? LastInput { get; private set; }

        public Task<NotificationDispatchAdmissionDto> ScheduleAsync(
            NotificationSendRequestedTenantEto input,
            CancellationToken cancellationToken = default) => CaptureAsync(input);

        public Task<NotificationDispatchAdmissionDto> ScheduleAsync(
            NotificationSendRequestedHostEto input,
            CancellationToken cancellationToken = default) => CaptureAsync(input);

        private Task<NotificationDispatchAdmissionDto> CaptureAsync(object input)
        {
            LastInput = input;
            var requestId = input switch
            {
                NotificationSendRequestedTenantEto tenant => tenant.RequestId,
                NotificationSendRequestedHostEto host => host.RequestId,
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new NotificationDispatchAdmissionDto(Guid.CreateVersion7(), requestId, false));
        }
    }
}
