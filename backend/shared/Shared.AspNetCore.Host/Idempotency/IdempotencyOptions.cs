namespace Shared.AspNetCore.Host.Idempotency;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    public string HeaderName { get; set; } = "Idempotency-Key";
    public int CompletedTtlHours { get; set; } = 24;
    public int InProgressTtlMinutes { get; set; } = 10;
}
