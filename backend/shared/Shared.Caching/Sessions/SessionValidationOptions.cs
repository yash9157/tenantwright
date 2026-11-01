namespace Shared.Caching.Sessions;

public sealed class SessionValidationOptions
{
    public bool Enabled { get; set; } = true;
    public bool Strict { get; set; } = true;
    public int MarkerTtlHours { get; set; } = 168;
    public bool EnableDbFallbackOnValkeyMiss { get; set; }
    public int IdleTimeoutMinutes { get; set; } = 30;
    public int MaxConcurrentSessions { get; set; }
    public bool CleanupEnabled { get; set; } = true;
    public int CleanupIntervalSeconds { get; set; } = 3600;
    public int RetentionDays { get; set; } = 30;
}
