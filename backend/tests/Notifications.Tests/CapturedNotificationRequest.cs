using Shared.EventContracts;

namespace Notifications.Tests;

internal sealed record CapturedNotificationRequest(
    Guid? TenantId,
    Guid RequestId,
    NotificationSendChannel Channel,
    string? Subject,
    string TemplateName,
    IReadOnlyDictionary<string, string?> TemplateModel,
    IReadOnlyList<NotificationRecipientEto> Recipients,
    string? Category,
    NotificationSendPriority Priority,
    string? ActionUrl,
    DateTime OccurredAtUtc,
    DateTime? ScheduledForUtc,
    string SourceService,
    bool RespectUserPreferences,
    Guid? PreferenceUserId,
    string? PreferenceKey)
{
    public static CapturedNotificationRequest From(NotificationSendRequestedTenantEto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Create(input.TenantId, input.RequestId, input.Channel, input.Subject, input.TemplateName,
            input.TemplateModel, input.Recipients, input.Category, input.Priority, input.ActionUrl,
            input.OccurredAtUtc, input.ScheduledForUtc, input.SourceService, input.RespectUserPreferences,
            input.PreferenceUserId, input.PreferenceKey);
    }

    public static CapturedNotificationRequest From(NotificationSendRequestedHostEto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Create(null, input.RequestId, input.Channel, input.Subject, input.TemplateName,
            input.TemplateModel, input.Recipients, input.Category, input.Priority, input.ActionUrl,
            input.OccurredAtUtc, input.ScheduledForUtc, input.SourceService, input.RespectUserPreferences,
            input.PreferenceUserId, input.PreferenceKey);
    }

    private static CapturedNotificationRequest Create(
        Guid? tenantId,
        Guid requestId,
        NotificationSendChannel channel,
        string? subject,
        string templateName,
        IReadOnlyDictionary<string, string?> templateModel,
        IReadOnlyList<NotificationRecipientEto> recipients,
        string? category,
        NotificationSendPriority priority,
        string? actionUrl,
        DateTime occurredAtUtc,
        DateTime? scheduledForUtc,
        string sourceService,
        bool respectUserPreferences,
        Guid? preferenceUserId,
        string? preferenceKey)
        => new(
            tenantId,
            requestId,
            channel,
            subject,
            templateName,
            templateModel,
            recipients,
            category,
            priority,
            actionUrl,
            occurredAtUtc,
            scheduledForUtc,
            sourceService,
            respectUserPreferences,
            preferenceUserId,
            preferenceKey);
}
