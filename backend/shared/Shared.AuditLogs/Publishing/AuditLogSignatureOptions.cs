namespace Shared.AuditLogs.Publishing;

public sealed class AuditLogSignatureOptions
{
    public const string SectionName = "AuditLogging:Signatures";

    public string? CurrentKeyId { get; set; }

    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    public bool RequireSignatures { get; set; }

    public TimeSpan FreshnessTolerance { get; set; } = TimeSpan.FromMinutes(5);
}
