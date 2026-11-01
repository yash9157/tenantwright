namespace Shared.BackgroundTasks.Jobs;

public sealed class BackgroundJobEnqueueOptions
{
    public BackgroundJobPriority Priority { get; init; } = BackgroundJobPriority.Normal;
    public TimeSpan? Delay { get; init; }
    public string? Queue { get; init; }
    public Guid? TenantId { get; init; }
    public string? CorrelationId { get; init; }
    public Guid? RequestedByUserId { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.Ordinal);
}

public sealed class RecurringBackgroundJobOptions
{
    public string? Queue { get; init; }
    public Guid? TenantId { get; init; }
    public string? CorrelationId { get; init; }
    public Guid? RequestedByUserId { get; init; }
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.Ordinal);
}
