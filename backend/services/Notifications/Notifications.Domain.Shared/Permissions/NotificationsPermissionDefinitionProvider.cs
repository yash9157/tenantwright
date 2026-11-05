using Permissions.Application.Contracts.Definitions;

namespace Shared.Permissions.Definitions.Notifications;

public class NotificationsPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(NotificationsPermissions.GroupName, "Notifications");

        var root = group.AddPermission(NotificationsPermissions.Default, "Notifications");

        var inbox = root.AddChild(NotificationsPermissions.Inbox.Default, "Inbox");
        inbox.AddChild(NotificationsPermissions.Inbox.Read, "Read Inbox");
        inbox.AddChild(NotificationsPermissions.Inbox.MarkRead, "Mark Inbox As Read");
        inbox.AddChild(NotificationsPermissions.Inbox.Archive, "Archive Inbox Notifications");
        inbox.AddChild(NotificationsPermissions.Inbox.Delete, "Delete Inbox Notifications");

        var query = root.AddChild(NotificationsPermissions.Query.Default, "Query Notifications");
        query.AddChild(NotificationsPermissions.Query.Read, "Read Notifications");
        query.AddChild(
            NotificationsPermissions.Query.ReadSensitivePayload,
            "Read Sensitive Notification Payloads");

        var policy = root.AddChild(NotificationsPermissions.Policy.Default, "Notification Delivery Policy");
        policy.AddChild(NotificationsPermissions.Policy.Read, "Read Notification Delivery Policy");
        policy.AddChild(NotificationsPermissions.Policy.Manage, "Manage Notification Delivery Policy");

        var deliveries = root.AddChild(NotificationsPermissions.Deliveries.Default, "Notification Deliveries");
        deliveries.AddChild(NotificationsPermissions.Deliveries.Resend, "Resend Failed Notification Deliveries");
        deliveries.AddChild(NotificationsPermissions.Deliveries.OverrideResend, "Resend Failed Notification Deliveries With Recipient Override");

        var config = root.AddChild(NotificationsPermissions.Config.Default, "Configuration");
        var dispatches = root.AddChild(NotificationsPermissions.Dispatches.Default, "Notification Dispatches");
        dispatches.AddChild(NotificationsPermissions.Dispatches.Cancel, "Cancel Unclaimed Notification Dispatch Work");
        // Provider credentials must be manageable before a channel can be enabled. Delivery remains
        // feature-gated through Notifications.Send.*; configuration itself is feature-neutral.
        config.AddChild(NotificationsPermissions.Config.Email, "Manage Email Provider Config");
        config.AddChild(NotificationsPermissions.Config.TestSend, "Send Notification Provider Test Message");
        config.AddChild(NotificationsPermissions.Config.Sms, "Manage SMS Provider Config");
        config.AddChild(NotificationsPermissions.Config.Push, "Manage Push Provider Config");
        config.AddChild(NotificationsPermissions.Config.WhatsApp, "Manage WhatsApp Provider Config");

        var templates = root.AddChild(NotificationsPermissions.Templates.Default, "Notification Templates");
        templates.AddChild(NotificationsPermissions.Templates.Read, "Read Notification Templates");
        templates.AddChild(NotificationsPermissions.Templates.Preview, "Preview Notification Templates");
        templates.AddChild(NotificationsPermissions.Templates.Create, "Create Notification Templates");
        templates.AddChild(NotificationsPermissions.Templates.Manage, "Manage Notification Template Drafts");
        templates.AddChild(NotificationsPermissions.Templates.Publish, "Publish Notification Template Revisions");
        templates.AddChild(NotificationsPermissions.Templates.Rename, "Rename Draft-Only Notification Template Keys");
        templates.AddChild(NotificationsPermissions.Templates.Archive, "Archive Or Restore Notification Templates");
        templates.AddChild(NotificationsPermissions.Templates.DeleteDraft, "Delete Draft-Only Notification Templates");

        var providers = root.AddChild(NotificationsPermissions.Providers.Default, "Notification Providers");
        providers.AddChild(NotificationsPermissions.Providers.Read, "Read Notification Provider Diagnostics");
        providers.AddChild(NotificationsPermissions.Providers.ActivityRead, "Read Notification Provider Activity");
        providers.AddChild(NotificationsPermissions.Providers.AnalyticsRead, "Read Notification Provider Analytics");

        var replay = root.AddChild(NotificationsPermissions.Replay.Default, "Notification Replay");
        replay.AddChild(NotificationsPermissions.Replay.Execute, "Replay Historical Notifications");
        replay.AddChild(NotificationsPermissions.Replay.OverridePreview, "Preview Notification Replay Overrides");
        replay.AddChild(NotificationsPermissions.Replay.OverrideExecute, "Execute Notification Replay Overrides");

        var presence = root.AddChild(NotificationsPermissions.Presence.Default, "Presence");
        presence.AddChild(NotificationsPermissions.Presence.Read, "Read Presence")
            .RequireFeatures(NotificationsFeatures.Realtime);
    }
}
