namespace Shared.Permissions.Definitions.Notifications;

public static class NotificationsPermissions
{
    public const string GroupName = "Notifications";

    public const string Default = "Notifications";

    public static class Inbox
    {
        public const string Default = "Notifications.Inbox";
        public const string Read = Default + ".Read";
        public const string MarkRead = Default + ".MarkRead";
        public const string Archive = Default + ".Archive";
        public const string Delete = Default + ".Delete";
    }

    public static class Query
    {
        public const string Default = "Notifications.Query";
        public const string Read = Default + ".Read";
        public const string ReadSensitivePayload = Default + ".ReadSensitivePayload";
    }

    public static class Policy
    {
        public const string Default = "Notifications.Policy";
        public const string Read = Default + ".Read";
        public const string Manage = Default + ".Manage";
    }

    public static class Deliveries
    {
        public const string Default = "Notifications.Deliveries";
        public const string Resend = Default + ".Resend";
        public const string OverrideResend = Default + ".OverrideResend";
    }

    public static class Config
    {
        public const string Default = "Notifications.Config";
        public const string Email = Default + ".Email";
        public const string TestSend = Default + ".TestSend";
        public const string Sms = Default + ".Sms";
        public const string Push = Default + ".Push";
        public const string WhatsApp = Default + ".WhatsApp";
    }

    public static class Templates
    {
        public const string Default = "Notifications.Templates";
        public const string Read = Default + ".Read";
        public const string Preview = Default + ".Preview";
        public const string Create = Default + ".Create";
        public const string Manage = Default + ".Manage";
        public const string Publish = Default + ".Publish";
        public const string Rename = Default + ".Rename";
        public const string Archive = Default + ".Archive";
        public const string DeleteDraft = Default + ".DeleteDraft";
    }

    public static class Providers
    {
        public const string Default = "Notifications.Providers";
        public const string Read = Default + ".Read";
        public const string ActivityRead = Default + ".ActivityRead";
        public const string AnalyticsRead = Default + ".AnalyticsRead";
    }

    public static class Replay
    {
        public const string Default = "Notifications.Replay";
        public const string Execute = Default + ".Execute";
        public const string OverridePreview = Default + ".OverridePreview";
        public const string OverrideExecute = Default + ".OverrideExecute";
    }

    public static class Presence
    {
        public const string Default = "Notifications.Presence";
        public const string Read = Default + ".Read";
    }
}
