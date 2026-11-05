namespace Notifications.Application.Dispatches;

public sealed class NotificationDispatchOptions
{
    public const string SectionName = "Notifications:Dispatch";
    public int MaximumRecipients { get; set; } = 100;
    public int MaximumDestinationsPerChannel { get; set; } = 10;
    public int MaximumTotalDestinations { get; set; } = 1000;
    public int MaximumPayloadBytes { get; set; } = 262144;
    public int DefaultLifetimeDays { get; set; } = 7;
    public int ReconciliationBatchSize { get; set; } = 100;
}
