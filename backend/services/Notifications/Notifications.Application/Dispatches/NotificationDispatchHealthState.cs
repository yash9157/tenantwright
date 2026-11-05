namespace Notifications.Application.Dispatches;

public sealed class NotificationDispatchHealthState(TimeProvider timeProvider)
{
    private long _lastSuccessTicks;
    private int _failed;

    public void Completed()
    {
        Interlocked.Exchange(ref _lastSuccessTicks, timeProvider.GetUtcNow().UtcTicks);
        Volatile.Write(ref _failed, 0);
    }

    public void Failed() => Volatile.Write(ref _failed, 1);

    public bool IsReady => Volatile.Read(ref _failed) == 0 && Interlocked.Read(ref _lastSuccessTicks) != 0 &&
        timeProvider.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastSuccessTicks) < TimeSpan.FromMinutes(5).Ticks;
}
