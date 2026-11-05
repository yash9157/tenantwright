namespace Shared.EventContracts;

public enum NotificationDeliveryMode
{
    Parallel = 1,
    OrderedFallback = 2
}

public sealed class NotificationDeliveryPlan
{
    public NotificationDeliveryMode Mode { get; set; } = NotificationDeliveryMode.Parallel;
    public List<NotificationChannelPlan> Steps { get; set; } = [];
}

public sealed class NotificationChannelPlan
{
    public NotificationSendChannel Channel { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public Dictionary<string, string?> TemplateModel { get; set; } = new(StringComparer.Ordinal);
}

public sealed class NotificationRecipientPlan
{
    public string Key { get; set; } = string.Empty;
    public Guid? PreferenceUserId { get; set; }
    public List<NotificationDestination> Destinations { get; set; } = [];
}

public sealed class NotificationDestination
{
    public NotificationSendChannel Channel { get; set; }
    public NotificationSendRecipientType RecipientType { get; set; }
    public string RecipientValue { get; set; } = string.Empty;
}
