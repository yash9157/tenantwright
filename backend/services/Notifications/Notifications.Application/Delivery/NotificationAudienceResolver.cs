using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Domain.Shared.Constants;
using Shared;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

public sealed class NotificationAudienceResolver : INotificationAudienceResolver, ITransientDependency
{
    private readonly IIdentityAudienceClient _identityAudienceClient;
    private readonly IOptions<NotificationAudienceOptions> _options;

    public NotificationAudienceResolver(
        IIdentityAudienceClient identityAudienceClient,
        IOptions<NotificationAudienceOptions> options)
    {
        _identityAudienceClient = identityAudienceClient;
        _options = options;
    }

    public async Task<IReadOnlyList<NotificationRecipientEto>> ResolveAsync(
        Guid? tenantId,
        NotificationSendChannel channel,
        IReadOnlyList<NotificationRecipientEto> recipients,
        CancellationToken cancellationToken = default)
    {
        recipients ??= [];
        if (!recipients.Any(IsGroupRecipient))
        {
            return recipients;
        }

        // Feature is in review + disabled by default: reject group sends fail-closed until sign-off.
        if (!_options.Value.Enabled)
        {
            throw new InvalidOperationException(
                "Recipient-group resolution is disabled (Notifications:Audience:Enabled). This feature is pending review sign-off.");
        }

        // Group sends resolve to App-channel users and require a tenant.
        if (channel != NotificationSendChannel.App)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.GroupChannelInvalid,
                "Group recipient types are only supported for the App channel.");
        }

        if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.AudienceTenantRequired,
                "A tenant is required to resolve group recipients.");
        }

        var maxResolved = Math.Clamp(_options.Value.MaxResolvedRecipients, 1, 100_000);
        var fetchCap = maxResolved + 1;
        var userIds = new HashSet<Guid>();

        foreach (var recipient in recipients)
        {
            switch (recipient.RecipientType)
            {
                case NotificationSendRecipientType.UserId:
                    if (Guid.TryParse(recipient.RecipientValue, out var literalUserId))
                    {
                        userIds.Add(literalUserId);
                    }

                    break;

                case NotificationSendRecipientType.Role:
                    AddRange(userIds, await _identityAudienceClient.GetUsersByRoleAsync(
                        tenantId.Value, recipient.RecipientValue, fetchCap, cancellationToken));
                    break;

                case NotificationSendRecipientType.AllTenantUsers:
                    AddRange(userIds, await _identityAudienceClient.GetTenantUserIdsAsync(
                        tenantId.Value, fetchCap, cancellationToken));
                    break;

                default:
                    throw new SharedValidationException(
                        NotificationErrorCodes.GroupRecipientInvalid,
                        $"Recipient type '{recipient.RecipientType}' is not valid for an App-channel group send.");
            }

            if (userIds.Count > maxResolved)
            {
                throw new SharedValidationException(
                    NotificationErrorCodes.AudienceLimitExceeded,
                    $"Resolved audience exceeds the maximum of {maxResolved} recipients.");
            }
        }

        return userIds
            .Select(id => new NotificationRecipientEto
            {
                RecipientType = NotificationSendRecipientType.UserId,
                RecipientValue = id.ToString("D")
            })
            .ToList();
    }

    private static bool IsGroupRecipient(NotificationRecipientEto recipient)
    {
        return recipient.RecipientType is NotificationSendRecipientType.Role
            or NotificationSendRecipientType.AllTenantUsers;
    }

    private static void AddRange(HashSet<Guid> set, IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
        {
            set.Add(id);
        }
    }
}
