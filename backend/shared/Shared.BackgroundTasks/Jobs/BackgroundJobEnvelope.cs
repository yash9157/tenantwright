namespace Shared.BackgroundTasks.Jobs;

public sealed class BackgroundJobEnvelope
{
    public string JobName { get; init; } = string.Empty;
    public string ArgsJson { get; init; } = string.Empty;
    public Guid? TenantId { get; init; }
    public string? CorrelationId { get; init; }
    public Guid? RequestedByUserId { get; init; }
    public DateTime EnqueuedAtUtc { get; init; }
    public BackgroundJobPriority Priority { get; init; } = BackgroundJobPriority.Normal;
    public string? Queue { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.Ordinal);
}
