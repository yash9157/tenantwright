using Permissions.Application.Contracts.Definitions;

namespace Shared.Permissions.Definitions.Notifications;

public class NotificationsFeatureDefinitionProvider : FeatureDefinitionProvider
{
    public override void Define(IFeatureDefinitionContext context)
    {
        var group = context.AddGroup(NotificationsFeatures.GroupName, "Notifications");

        // Each channel is an independent on/off entitlement paired with its own monthly cap — a
        // tenant can enable any combination of channels, and each channel carries its own limit,
        // range, and unit. The cap names sort directly after their channel toggle.
        var email = group.AddFeature(NotificationsFeatures.Email, "Email");
        email.DefaultValue = "true";
        email.WithDescription("Send tenant notifications over email.");

        var emailCap = group.AddFeature(NotificationsFeatures.EmailMonthlyCap, "Email monthly cap");
        emailCap
            .AsNumber(minValue: 0, maxValue: 1_000_000, unit: "emails")
            .WithDescription("Maximum emails this tenant may send per month.")
            .Requires(NotificationsFeatures.Email);
        emailCap.DefaultValue = "50000";

        var sms = group.AddFeature(NotificationsFeatures.Sms, "SMS");
        sms.DefaultValue = "false";
        sms.WithDescription("Send notifications as SMS text messages.");

        var smsCap = group.AddFeature(NotificationsFeatures.SmsMonthlyCap, "SMS monthly cap");
        smsCap
            .AsNumber(minValue: 0, maxValue: 100_000, unit: "messages")
            .WithDescription("Maximum SMS messages this tenant may send per month.")
            .Requires(NotificationsFeatures.Sms);
        smsCap.DefaultValue = "5000";

        var push = group.AddFeature(NotificationsFeatures.Push, "Push");
        push.DefaultValue = "false";
        push.WithDescription("Deliver push notifications to mobile and web clients.");

        var pushCap = group.AddFeature(NotificationsFeatures.PushMonthlyCap, "Push monthly cap");
        pushCap
            .AsNumber(minValue: 0, maxValue: 5_000_000, unit: "notifications")
            .WithDescription("Maximum push notifications this tenant may send per month.")
            .Requires(NotificationsFeatures.Push);
        pushCap.DefaultValue = "100000";

        var whatsApp = group.AddFeature(NotificationsFeatures.WhatsApp, "WhatsApp");
        whatsApp.DefaultValue = "false";
        whatsApp.WithDescription("Deliver notifications through the WhatsApp Business channel.");

        var whatsAppCap = group.AddFeature(NotificationsFeatures.WhatsAppMonthlyCap, "WhatsApp monthly cap");
        whatsAppCap
            .AsNumber(minValue: 0, maxValue: 200_000, unit: "messages")
            .WithDescription("Maximum WhatsApp messages this tenant may send per month.")
            .Requires(NotificationsFeatures.WhatsApp);
        whatsAppCap.DefaultValue = "10000";

        var realtime = group.AddFeature(NotificationsFeatures.Realtime, "Realtime");
        realtime.DefaultValue = "true";
        realtime.WithDescription("Stream in-app realtime notifications to connected sessions.");

        // Single-choice fallback channel — exercises the Selection value type (dropdown in the editor).
        var defaultChannel = group.AddFeature(NotificationsFeatures.DefaultChannel, "Default channel");
        defaultChannel
            .AsSelection(
                NotificationsFeatures.ChannelEmail,
                NotificationsFeatures.ChannelSms,
                NotificationsFeatures.ChannelPush)
            .WithDescription("The channel used when a notification does not specify one.");
        defaultChannel.DefaultValue = NotificationsFeatures.ChannelEmail;
    }
}
