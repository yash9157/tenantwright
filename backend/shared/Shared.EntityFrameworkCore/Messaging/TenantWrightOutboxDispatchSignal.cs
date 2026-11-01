namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>
/// In-process wake-up for the outbox dispatcher, raised after a unit of work that wrote outbox rows commits. Coalesces
/// any number of notifications into one pending wake-up; the poll interval remains the cross-replica fallback.
/// </summary>
public sealed class TenantWrightOutboxDispatchSignal : IDisposable
{
    private readonly SemaphoreSlim _pending = new(0, 1);

    public void Notify()
    {
        try
        {
            _pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending; the next claim will see every committed row.
        }
    }

    /// <summary>Waits for a wake-up or until <paramref name="timeout"/> elapses.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _pending.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _pending.Dispose();
}
