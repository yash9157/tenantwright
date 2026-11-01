using Shared.BackgroundTasks.Jobs;

namespace AuditLogs.Tests.TestDoubles;

/// <summary>
/// Accepts every enqueue and does nothing, for tests that construct <c>AuditLogQueryAppService</c> but do
/// not exercise its background work (exports and archive restores are the two paths that enqueue).
/// </summary>
/// <remarks>
/// Shared rather than private-per-file: three query-service test classes had identical private copies of
/// this before, and a fourth was about to be added.
/// </remarks>
public sealed class NoopBackgroundJobManager : IBackgroundJobManager
{
    public Task<string> EnqueueAsync<TArgs>(
        TArgs args,
        BackgroundJobEnqueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult("noop");
    }
}
