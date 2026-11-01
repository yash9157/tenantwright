namespace Shared.AuditLogs.AspNetCore;

public sealed class SharedAspNetCoreAuditingOptions
{
    public List<string> IgnoredUrls { get; } =
    [
        "/health",
        "/alive",
        "/metrics",
        "/connect/token",
        "/connect/authorize",
        "/connect/logout",
        "/connect/revoke"
    ];
}
