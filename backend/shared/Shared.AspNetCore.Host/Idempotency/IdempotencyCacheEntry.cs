using Shared.Caching;

namespace Shared.AspNetCore.Host.Idempotency;

public enum IdempotencyRecordState
{
    InProgress = 0,
    Completed = 1
}

[IgnoreCacheMultiTenancy]
public sealed class IdempotencyCacheEntry
{
    public IdempotencyRecordState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? ResponseBody { get; set; }
}
