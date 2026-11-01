using Shared.EventContracts;

namespace Notifications.Application.Contracts.Delivery;

public interface INotificationAudienceResolver
{
    /// <summary>
    /// Expands group recipient types (Role / AllTenantUsers) into concrete UserId recipients via
    /// Identity. Returns the input unchanged when there are no group recipients. Fail-closed: throws
    /// on unreachable Identity, unsupported group type, or over-cap audience.
    /// </summary>
    Task<IReadOnlyList<NotificationRecipientEto>> ResolveAsync(
        Guid? tenantId,
        NotificationSendChannel channel,
        IReadOnlyList<NotificationRecipientEto> recipients,
        CancellationToken cancellationToken = default);
}
