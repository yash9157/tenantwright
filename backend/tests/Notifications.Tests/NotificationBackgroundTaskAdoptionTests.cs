using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Application.Contracts.Providers;
using Notifications.Application.Delivery;
using Notifications.Delivery;
using Notifications.Workers;
using Shared.BackgroundTasks.Workers;
using Xunit;

namespace Notifications.Tests;

public class NotificationBackgroundTaskAdoptionTests
{
    [Fact]
    public void Notification_delivery_worker_uses_shared_periodic_worker_contract()
    {
        Assert.True(typeof(IPeriodicBackgroundWorker).IsAssignableFrom(typeof(NotificationDeliveryBackgroundWorker)));
        Assert.Equal("notifications-delivery", NotificationDeliveryBackgroundWorker.WorkerName);
    }

    [Fact]
    public async Task Notification_delivery_worker_should_repeat_immediately_when_batch_processed()
    {
        var worker = new NotificationDeliveryBackgroundWorker(
            new FixedDeliveryWorker(processedCount: 3), SharedOnlyTenantDatabases.FanOut(),
            new CapturingWakeSignal(),
            new FixedOptionsMonitor<NotificationsDeliveryOptions>(new NotificationsDeliveryOptions()),
            NullLogger<NotificationDeliveryBackgroundWorker>.Instance, new IdleDispatchCoordinator(), new global::Notifications.Application.Dispatches.NotificationDispatchHealthState(TimeProvider.System));

        var result = await worker.ExecuteAsync(CreateContext());

        Assert.True(result.RunAgainImmediately);
        Assert.Null(result.WaitAsync);
    }

    [Fact]
    public async Task Notification_delivery_worker_should_wait_on_signal_when_idle()
    {
        var wakeSignal = new CapturingWakeSignal();
        var worker = new NotificationDeliveryBackgroundWorker(
            new FixedDeliveryWorker(processedCount: 0), SharedOnlyTenantDatabases.FanOut(),
            wakeSignal,
            new FixedOptionsMonitor<NotificationsDeliveryOptions>(new NotificationsDeliveryOptions()),
            NullLogger<NotificationDeliveryBackgroundWorker>.Instance, new IdleDispatchCoordinator(), new global::Notifications.Application.Dispatches.NotificationDispatchHealthState(TimeProvider.System));

        var result = await worker.ExecuteAsync(CreateContext());

        Assert.False(result.RunAgainImmediately);
        Assert.NotNull(result.WaitAsync);

        await result.WaitAsync!(CancellationToken.None);

        Assert.Equal(1, wakeSignal.WaitCount);
        Assert.Equal(NotificationDeliveryWakeKeys.Delivery, wakeSignal.LastKey);
        Assert.Equal(TimeSpan.FromSeconds(30), wakeSignal.LastOptions?.SafetyPollTimeout);
    }

    [Fact]
    public void Notification_delivery_latency_rollup_uses_shared_periodic_worker_contract_and_stable_ownership_lock()
    {
        Assert.True(typeof(IPeriodicBackgroundWorker).IsAssignableFrom(typeof(NotificationDeliveryLatencyRollupBackgroundWorker)));
        Assert.Equal("notifications-delivery-latency-rollup", NotificationDeliveryLatencyRollupBackgroundWorker.WorkerName);
        Assert.Equal(
            "notifications:delivery-latency-rollup:owner",
            NotificationDeliveryLatencyRollupBackgroundWorker.OwnershipLockName);
    }

    [Fact]
    public async Task Notification_delivery_latency_rollup_repeats_only_while_unrolled_backlog_remains()
    {
        var worker = new NotificationDeliveryLatencyRollupBackgroundWorker(
            new FixedLatencyRollupProcessor(new NotificationDeliveryLatencyRollupBatchResult(24, true)), SharedOnlyTenantDatabases.FanOut(),
            new FixedOptionsMonitor<NotificationsDeliveryLatencyRollupOptions>(
                new NotificationsDeliveryLatencyRollupOptions { IntervalSeconds = 300 }),
            NullLogger<NotificationDeliveryLatencyRollupBackgroundWorker>.Instance);

        var result = await worker.ExecuteAsync(CreateContext());

        Assert.True(result.RunAgainImmediately);
        Assert.Null(result.WaitAsync);
    }

    [Fact]
    public async Task Notification_delivery_latency_rollup_waits_after_refreshing_its_current_backlog()
    {
        var worker = new NotificationDeliveryLatencyRollupBackgroundWorker(
            new FixedLatencyRollupProcessor(new NotificationDeliveryLatencyRollupBatchResult(24, false)), SharedOnlyTenantDatabases.FanOut(),
            new FixedOptionsMonitor<NotificationsDeliveryLatencyRollupOptions>(
                new NotificationsDeliveryLatencyRollupOptions { IntervalSeconds = 300 }),
            NullLogger<NotificationDeliveryLatencyRollupBackgroundWorker>.Instance);

        var result = await worker.ExecuteAsync(CreateContext());

        Assert.False(result.RunAgainImmediately);
        Assert.NotNull(result.WaitAsync);
    }

    private static BackgroundWorkerContext CreateContext()
    {
        return new BackgroundWorkerContext(
            serviceProvider: new EmptyServiceProvider(),
            workerName: NotificationDeliveryBackgroundWorker.WorkerName,
            consecutiveFailureCount: 0,
            iterationStartedAtUtc: DateTime.UtcNow,
            cancellationToken: CancellationToken.None);
    }

    private sealed class IdleDispatchCoordinator : INotificationDispatchCoordinator
    {
        public Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class FixedDeliveryWorker : INotificationDeliveryWorker
    {
        private readonly int _processedCount;

        public FixedDeliveryWorker(int processedCount)
        {
            _processedCount = processedCount;
        }

        public Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_processedCount);
        }
    }

    private sealed class FixedLatencyRollupProcessor : INotificationDeliveryLatencyRollupProcessor
    {
        private readonly NotificationDeliveryLatencyRollupBatchResult _result;

        public FixedLatencyRollupProcessor(NotificationDeliveryLatencyRollupBatchResult result)
        {
            _result = result;
        }

        public Task<NotificationDeliveryLatencyRollupBatchResult> RollupBatchAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class CapturingWakeSignal : IBackgroundWorkerWakeSignal
    {
        public int WaitCount { get; private set; }
        public string? LastKey { get; private set; }
        public BackgroundWorkerWakeOptions? LastOptions { get; private set; }

        public Task SignalAsync(
            string key,
            BackgroundWorkerWakeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<BackgroundWorkerWakeResult> WaitAsync(
            string key,
            BackgroundWorkerWakeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WaitCount++;
            LastKey = key;
            LastOptions = options;
            return Task.FromResult(BackgroundWorkerWakeResult.SafetyPollTimeout);
        }
    }

    private sealed class FixedOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    {
        public FixedOptionsMonitor(TOptions currentValue)
        {
            CurrentValue = currentValue;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
