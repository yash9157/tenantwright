using Microsoft.Extensions.Logging.Abstractions;
using Shared.BackgroundTasks.Workers;
using Xunit;

namespace Shared.BackgroundTasks.Tests;

public class BackgroundWorkerWakeSignalTests
{
    [Fact]
    public async Task WaitAsync_should_unblock_when_signal_is_sent()
    {
        var wakeSignal = CreateWakeSignal();
        var options = new BackgroundWorkerWakeOptions
        {
            WakeupMode = BackgroundWorkerWakeMode.LocalOnly,
            SafetyPollTimeout = TimeSpan.FromSeconds(2)
        };

        var waitTask = wakeSignal.WaitAsync("test:worker", options);

        await wakeSignal.SignalAsync("test:worker", options);

        Assert.Equal(BackgroundWorkerWakeResult.Signal, await waitTask);
    }

    [Fact]
    public async Task WaitAsync_should_return_safety_timeout_when_no_signal_arrives()
    {
        var wakeSignal = CreateWakeSignal();

        var result = await wakeSignal.WaitAsync(
            "test:worker",
            new BackgroundWorkerWakeOptions
            {
                WakeupMode = BackgroundWorkerWakeMode.LocalOnly,
                SafetyPollTimeout = TimeSpan.FromMilliseconds(50)
            });

        Assert.Equal(BackgroundWorkerWakeResult.SafetyPollTimeout, result);
    }

    [Fact]
    public async Task WaitAsync_should_honor_cancellation()
    {
        var wakeSignal = CreateWakeSignal();
        using var cancellation = new CancellationTokenSource();

        var waitTask = wakeSignal.WaitAsync(
            "test:worker",
            new BackgroundWorkerWakeOptions
            {
                WakeupMode = BackgroundWorkerWakeMode.LocalOnly,
                SafetyPollTimeout = TimeSpan.FromSeconds(30)
            },
            cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waitTask);
    }

    [Fact]
    public async Task SignalAsync_when_valkey_is_unavailable_should_still_wake_local_waiters()
    {
        var wakeSignal = CreateWakeSignal();
        var options = new BackgroundWorkerWakeOptions
        {
            WakeupMode = BackgroundWorkerWakeMode.ValkeyPubSub,
            ValkeyChannel = "test:wakeup",
            SafetyPollTimeout = TimeSpan.FromSeconds(2)
        };

        var waitTask = wakeSignal.WaitAsync("test:worker", options);

        await wakeSignal.SignalAsync("test:worker", options);

        Assert.Equal(BackgroundWorkerWakeResult.Signal, await waitTask);
    }

    private static BackgroundWorkerWakeSignal CreateWakeSignal()
    {
        return new BackgroundWorkerWakeSignal(NullLogger<BackgroundWorkerWakeSignal>.Instance);
    }
}
