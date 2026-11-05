using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.RateLimiting;

/// <summary>
/// Thrown when a schedule request would exceed the configured send quota. Callers must surface this
/// (never silently drop the request); the scheduler records a metric before throwing.
/// </summary>
public sealed class NotificationRateLimitExceededException : Exception
{
    public NotificationRateLimitExceededException(string scope, NotificationChannel channel, long limit, long observed, DateTime retryAtUtc)
        : base($"Notification rate limit exceeded for scope '{scope}' channel '{channel}': {observed} > {limit}.")
    {
        Scope = scope;
        Channel = channel;
        Limit = limit;
        Observed = observed;
        RetryAtUtc = retryAtUtc;
    }

    /// <summary>"tenant" or "global".</summary>
    public string Scope { get; }

    public NotificationChannel Channel { get; }

    public long Limit { get; }

    public long Observed { get; }
    public DateTime RetryAtUtc { get; }
}
