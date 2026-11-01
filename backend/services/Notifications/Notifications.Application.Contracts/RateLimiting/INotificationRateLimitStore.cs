namespace Notifications.Application.Contracts.RateLimiting;

/// <summary>
/// Backing counter for the rate limiter. Implementations increment a per-window counter for the key
/// and return the running total within the current window.
/// </summary>
public interface INotificationRateLimitStore
{
    Task<long> IncrementAndGetAsync(string key, TimeSpan window, int amount, CancellationToken cancellationToken = default);
}
