using Notifications.Domain.Shared.Constants;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Events;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<PermissionGrantApprovalRequestedEto>),
    IncludeSelf = true)]
public sealed class PermissionGrantApprovalRequestedEventHandler
    : ITenantWrightMessageHandler<PermissionGrantApprovalRequestedEto>,
        ITransientDependency
{
    private readonly NotificationSendRequestedEventHandler _notificationHandler;
    private readonly ILogger<PermissionGrantApprovalRequestedEventHandler> _logger;

    public PermissionGrantApprovalRequestedEventHandler(
        NotificationSendRequestedEventHandler notificationHandler,
        ILogger<PermissionGrantApprovalRequestedEventHandler> logger)
    {
        _notificationHandler = notificationHandler;
        _logger = logger;
    }

    public async Task HandleAsync(
        PermissionGrantApprovalRequestedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (!eventData.TenantId.HasValue || eventData.TenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Permission grant approval requests require a non-empty tenant identifier.");
        }

        var recipients = eventData.ApproverUserIds
            .Select(id => new NotificationRecipientEto
            {
                RecipientType = NotificationSendRecipientType.UserId,
                RecipientValue = id.ToString("D")
            })
            .Concat(eventData.ApproverRoleNames.Select(role => new NotificationRecipientEto
            {
                RecipientType = NotificationSendRecipientType.Role,
                RecipientValue = role
            }))
            .DistinctBy(recipient => (recipient.RecipientType, recipient.RecipientValue))
            .ToList();
        if (recipients.Count == 0)
        {
            _logger.LogWarning(
                "Permission approval request {RequestId} has no resolvable approver audience.",
                eventData.RequestId);
            return;
        }

        await _notificationHandler.HandleAsync(new NotificationSendRequestedTenantEto
        {
            RequestId = eventData.RequestId,
            TenantId = eventData.TenantId.Value,
            Channel = NotificationSendChannel.App,
            Subject = "Sensitive access request needs review",
            TemplateName = NotificationsTemplateNames.Permissions.GrantApprovalRequested,
            TemplateModel = new(StringComparer.Ordinal)
            {
                ["requester_display_name"] = eventData.RequesterDisplayName,
                ["permission_name"] = eventData.PermissionName,
                ["provider_display_name"] = eventData.ProviderDisplayName,
                ["reason"] = eventData.Reason,
                ["ticket_id"] = eventData.TicketId
            },
            Recipients = recipients,
            Category = "permissions.approval.requested",
            Priority = NotificationSendPriority.High,
            ActionUrl = "/platform/access/approvals",
            OccurredAtUtc = eventData.OccurredAtUtc,
            SourceService = "Permissions",
            RespectUserPreferences = true,
            PreferenceKey = "permissions.approvals"
        }, cancellationToken);
    }
}

[ExposeServices(
    typeof(ITenantWrightMessageHandler<PermissionGrantApprovalDecidedEto>),
    IncludeSelf = true)]
public sealed class PermissionGrantApprovalDecidedEventHandler
    : ITenantWrightMessageHandler<PermissionGrantApprovalDecidedEto>,
        ITransientDependency
{
    private readonly NotificationSendRequestedEventHandler _notificationHandler;

    public PermissionGrantApprovalDecidedEventHandler(NotificationSendRequestedEventHandler notificationHandler)
    {
        _notificationHandler = notificationHandler;
    }

    public Task HandleAsync(
        PermissionGrantApprovalDecidedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (!eventData.TenantId.HasValue || eventData.TenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Permission grant approval decisions require a non-empty tenant identifier.");
        }

        if (!eventData.RequesterUserId.HasValue)
        {
            return Task.CompletedTask;
        }

        return _notificationHandler.HandleAsync(new NotificationSendRequestedTenantEto
        {
            RequestId = eventData.RequestId,
            TenantId = eventData.TenantId.Value,
            Channel = NotificationSendChannel.App,
            Subject = $"Sensitive access request {eventData.Decision.ToLowerInvariant()}",
            TemplateName = NotificationsTemplateNames.Permissions.GrantApprovalDecided,
            TemplateModel = new(StringComparer.Ordinal)
            {
                ["permission_name"] = eventData.PermissionName,
                ["provider_display_name"] = eventData.ProviderDisplayName,
                ["decision"] = eventData.Decision.ToLowerInvariant(),
                ["decision_reason"] = eventData.DecisionReason,
                ["decider_display_name"] = eventData.DeciderDisplayName
            },
            Recipients =
            [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.UserId,
                    RecipientValue = eventData.RequesterUserId.Value.ToString("D")
                }
            ],
            Category = "permissions.approval.decided",
            Priority = NotificationSendPriority.Normal,
            ActionUrl = "/account/access",
            OccurredAtUtc = eventData.OccurredAtUtc,
            SourceService = "Permissions",
            RespectUserPreferences = true,
            PreferenceUserId = eventData.RequesterUserId,
            PreferenceKey = "permissions.approvals"
        }, cancellationToken);
    }
}
