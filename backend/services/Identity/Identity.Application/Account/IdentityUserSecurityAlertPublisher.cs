using System.Diagnostics.Metrics;
using Identity.Application.Contracts.Account.Risk;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.Extensions.Logging;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Identity.Application.Account;

public sealed class IdentityUserSecurityAlertPublisher : ITransientDependency
{
    private static readonly Meter SecurityAlertMeter = new("Identity.SecurityAlerts");
    private static readonly Counter<long> EnqueueFailureCounter =
        SecurityAlertMeter.CreateCounter<long>("identity.security_alert.enqueue.failure.count");

    private const string NewDeviceTemplateName = "Identity.NewDeviceSignInAlert";
    private const string RiskBlockedTemplateName = "Identity.SecurityRiskBlockedAlert";
    private const string SessionRevokedTemplateName = "Identity.AdminSessionRevokedAlert";
    private const string MfaChangedTemplateName = "Identity.MfaChangedAlert";

    private readonly ITenantWrightMessageSender _eventBus;
    private readonly ILogger<IdentityUserSecurityAlertPublisher> _logger;

    public IdentityUserSecurityAlertPublisher(
        ITenantWrightMessageSender eventBus,
        ILogger<IdentityUserSecurityAlertPublisher> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task PublishNewDeviceSignInAlertAsync(
        Guid? tenantId,
        Guid userId,
        string? userName,
        string? email,
        string? clientIpAddress,
        string? clientId,
        string? browserInfo,
        string? location,
        DateTime occurredAtUtc)
    {
        return PublishOptionalEmailAlertAsync(
            tenantId,
            userId,
            email,
            IdentityUserNotificationAlertKeys.NewDeviceSignIn,
            NewDeviceTemplateName,
            "New device sign-in detected",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userId"] = userId.ToString("D"),
                ["userName"] = userName,
                ["clientIpAddress"] = clientIpAddress,
                ["clientId"] = clientId,
                ["browserInfo"] = browserInfo,
                ["location"] = location
            },
            occurredAtUtc);
    }

    public Task PublishRiskBlockedAlertAsync(
        Guid? tenantId,
        Guid userId,
        string? userName,
        string? email,
        string? clientIpAddress,
        string? clientId,
        LoginRiskEvaluationResult riskEvaluation,
        DateTime occurredAtUtc)
    {
        return PublishOptionalEmailAlertAsync(
            tenantId,
            userId,
            email,
            IdentityUserNotificationAlertKeys.RiskBlocked,
            RiskBlockedTemplateName,
            "Suspicious sign-in blocked",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userId"] = userId.ToString("D"),
                ["userName"] = userName,
                ["riskLevel"] = riskEvaluation.Level.ToString(),
                ["riskAction"] = riskEvaluation.Action.ToString(),
                ["riskScore"] = riskEvaluation.Score.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                ["riskReasons"] = string.Join(',', riskEvaluation.Reasons),
                ["clientIpAddress"] = clientIpAddress,
                ["clientId"] = clientId
            },
            occurredAtUtc);
    }

    public Task PublishAdminSessionRevokedAlertAsync(
        Guid? tenantId,
        Guid userId,
        string? userName,
        string? email,
        string? action,
        string? revocationReason,
        DateTime occurredAtUtc)
    {
        return PublishOptionalEmailAlertAsync(
            tenantId,
            userId,
            email,
            IdentityUserNotificationAlertKeys.AdminSessionRevoked,
            SessionRevokedTemplateName,
            "Your active sessions were revoked",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userId"] = userId.ToString("D"),
                ["userName"] = userName,
                ["action"] = action,
                ["revocationReason"] = revocationReason
            },
            occurredAtUtc);
    }

    public Task PublishMfaChangedAlertAsync(
        Guid? tenantId,
        Guid userId,
        string? userName,
        string? email,
        string changeType,
        DateTime occurredAtUtc)
    {
        return PublishOptionalEmailAlertAsync(
            tenantId,
            userId,
            email,
            IdentityUserNotificationAlertKeys.MfaChanged,
            MfaChangedTemplateName,
            "Multi-factor authentication changed",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userId"] = userId.ToString("D"),
                ["userName"] = userName,
                ["changeType"] = changeType
            },
            occurredAtUtc);
    }

    private async Task PublishOptionalEmailAlertAsync(
        Guid? tenantId,
        Guid userId,
        string? email,
        string alertKey,
        string templateName,
        string subject,
        Dictionary<string, string?> templateModel,
        DateTime occurredAtUtc)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var command = new NotificationSendRequestedHostEto
        {
            RequestId = Guid.CreateVersion7(),
            OccurredAtUtc = occurredAtUtc,
            SourceService = "identity-service",
            RespectUserPreferences = true,
            PreferenceKey = alertKey,
            DeliveryPlan = new NotificationDeliveryPlan
            {
                Mode = NotificationDeliveryMode.Parallel,
                Steps = [new NotificationChannelPlan
                    {
                        Channel = NotificationSendChannel.Email,
                        Subject = subject,
                        TemplateName = templateName,
                        TemplateModel = templateModel
                    }]
            },
            Recipients = IdentityNotificationContractPublisher.CreateSingleChannelRecipients(NotificationSendChannel.Email, [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = email
                }
            ], userId),
        };

        try
        {
            await IdentityNotificationContractPublisher.PublishAsync(_eventBus, tenantId, command);
        }
        catch (Exception ex)
        {
            EnqueueFailureCounter.Add(1, new KeyValuePair<string, object?>("alert_key", alertKey));
            _logger.LogError(
                ex,
                "Security alert '{AlertKey}' for user {UserId} could not be staged in the notification outbox.",
                alertKey,
                userId);
        }
    }
}
