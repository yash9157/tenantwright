using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Workers;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.Uow.Abstractions;
using Shared.Uow.Context;
using Shared.Uow.Implementations;
using Shared.Uow.Options;
using Xunit;

namespace Shared.BackgroundTasks.Tests;

public class BackgroundWorkerOptionsTests
{
    [Fact]
    public void Normalize_WhenValuesOutOfRange_ClampsToBoundedValues()
    {
        var options = new BackgroundWorkerOptions
        {
            PollIntervalMs = 1,
            ErrorDelayMs = 1,
            JitterMs = -10,
            MaxConsecutiveFailures = 0,
            OwnershipAcquireTimeoutMs = 0,
            OwnershipLeaseMs = 1
        };

        BackgroundWorkerOptionsPostConfigure.Normalize(options);

        Assert.Equal(250, options.PollIntervalMs);
        Assert.Equal(250, options.ErrorDelayMs);
        Assert.Equal(0, options.JitterMs);
        Assert.Equal(1, options.MaxConsecutiveFailures);
        Assert.Equal(1, options.OwnershipAcquireTimeoutMs);
        Assert.Equal(500, options.OwnershipLeaseMs);
    }

    [Fact]
    public void Normalize_WhenPollIntervalExceedsOneDay_ClampsToOneDay()
    {
        var options = new BackgroundWorkerOptions
        {
            PollIntervalMs = int.MaxValue
        };

        BackgroundWorkerOptionsPostConfigure.Normalize(options);

        Assert.Equal(86_400_000, options.PollIntervalMs);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDistributedLockIsUnavailable_SkipsWorkerIteration()
    {
        var state = new WorkerState();
        var distributedLock = new FakeDistributedLock(returnHandle: false);
        var service = CreateService<CountingWorker>(
            state,
            distributedLock,
            new BackgroundWorkerOptions
            {
                WorkerName = "locked-worker",
                DistributedLockName = "shared:locked-worker",
                PollIntervalMs = 250
            });

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => distributedLock.TryAcquireCount > 0);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, state.Executions);
        Assert.True(distributedLock.TryAcquireCount >= 1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkerFails_ReusesBoundedBackoffAndPassesFailureCount()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var state = new WorkerState { StopSource = cancellation };
        var service = CreateService<FailTwiceWorker>(
            state,
            new FakeDistributedLock(returnHandle: true),
            new BackgroundWorkerOptions
            {
                WorkerName = "retry-worker",
                PollIntervalMs = 250,
                ErrorDelayMs = 250
            });

        await service.StartAsync(cancellation.Token);
        await WaitUntilAsync(() => state.Executions >= 3);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal([0, 1, 2], state.ObservedConsecutiveFailures);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStopped_PassesCancellationToWorker()
    {
        var state = new WorkerState();
        var service = CreateService<CancellationAwareWorker>(
            state,
            new FakeDistributedLock(returnHandle: true),
            new BackgroundWorkerOptions
            {
                WorkerName = "cancel-worker",
                PollIntervalMs = 250
            });

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Executions == 1);
        await service.StopAsync(CancellationToken.None);

        Assert.True(state.CancellationObserved);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkerReportsWorkCompleted_RunsNextIterationWithoutDelay()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var state = new WorkerState { StopSource = cancellation };
        var service = CreateService<ImmediateRepeatWorker>(
            state,
            new FakeDistributedLock(returnHandle: true),
            new BackgroundWorkerOptions
            {
                WorkerName = "immediate-worker",
                PollIntervalMs = 60_000
            });

        await service.StartAsync(cancellation.Token);
        await WaitUntilAsync(() => state.Executions >= 2);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, state.Executions);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkerProvidesSignalWait_UsesCustomWaitBeforeNextIteration()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var state = new WorkerState { StopSource = cancellation };
        var service = CreateService<SignalWaitWorker>(
            state,
            new FakeDistributedLock(returnHandle: true),
            new BackgroundWorkerOptions
            {
                WorkerName = "signal-worker",
                PollIntervalMs = 60_000
            });

        await service.StartAsync(cancellation.Token);
        await WaitUntilAsync(() => state.WaitStarted.Task.IsCompleted);

        Assert.Equal(1, state.Executions);

        state.AllowNextIteration.TrySetResult();
        await WaitUntilAsync(() => state.Executions >= 2);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, state.Executions);
    }

    private static SharedPeriodicBackgroundService<TWorker, BackgroundWorkerOptions> CreateService<TWorker>(
        WorkerState state,
        IDistributedLock distributedLock,
        BackgroundWorkerOptions options)
        where TWorker : class, IPeriodicBackgroundWorker
    {
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddOptions<UnitOfWorkDefaultOptions>();
        services.AddScoped<TWorker>();
        var provider = services.BuildServiceProvider();

        return new SharedPeriodicBackgroundService<TWorker, BackgroundWorkerOptions>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            distributedLock,
            new FixedOptionsMonitor<BackgroundWorkerOptions>(options),
            NullLogger<SharedPeriodicBackgroundService<TWorker, BackgroundWorkerOptions>>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        while (!predicate())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class CountingWorker : IPeriodicBackgroundWorker
    {
        private readonly WorkerState _state;

        public CountingWorker(WorkerState state)
        {
            _state = state;
        }

        public Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
        {
            _state.Executions++;
            return Task.FromResult(BackgroundWorkerExecutionResult.Idle);
        }
    }

    private sealed class FailTwiceWorker : IPeriodicBackgroundWorker
    {
        private readonly WorkerState _state;

        public FailTwiceWorker(WorkerState state)
        {
            _state = state;
        }

        public Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
        {
            _state.Executions++;
            _state.ObservedConsecutiveFailures.Add(context.ConsecutiveFailureCount);
            if (_state.Executions <= 2)
            {
                throw new InvalidOperationException("Simulated worker failure.");
            }

            _state.StopSource!.Cancel();
            return Task.FromResult(BackgroundWorkerExecutionResult.Idle);
        }
    }

    private sealed class CancellationAwareWorker : IPeriodicBackgroundWorker
    {
        private readonly WorkerState _state;

        public CancellationAwareWorker(WorkerState state)
        {
            _state = state;
        }

        public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
        {
            _state.Executions++;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                _state.CancellationObserved = true;
            }

            return BackgroundWorkerExecutionResult.Idle;
        }
    }

    private sealed class ImmediateRepeatWorker : IPeriodicBackgroundWorker
    {
        private readonly WorkerState _state;

        public ImmediateRepeatWorker(WorkerState state)
        {
            _state = state;
        }

        public Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
        {
            _state.Executions++;
            if (_state.Executions >= 2)
            {
                _state.StopSource!.Cancel();
                return Task.FromResult(BackgroundWorkerExecutionResult.Idle);
            }

            return Task.FromResult(BackgroundWorkerExecutionResult.WorkCompleted);
        }
    }

    private sealed class SignalWaitWorker : IPeriodicBackgroundWorker
    {
        private readonly WorkerState _state;

        public SignalWaitWorker(WorkerState state)
        {
            _state = state;
        }

        public Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
        {
            _state.Executions++;
            if (_state.Executions >= 2)
            {
                _state.StopSource!.Cancel();
                return Task.FromResult(BackgroundWorkerExecutionResult.Idle);
            }

            return Task.FromResult(BackgroundWorkerExecutionResult.Wait(async cancellationToken =>
            {
                _state.WaitStarted.TrySetResult();
                await _state.AllowNextIteration.Task.WaitAsync(cancellationToken);
            }));
        }
    }

    private sealed class WorkerState
    {
        public int Executions { get; set; }
        public bool CancellationObserved { get; set; }
        public CancellationTokenSource? StopSource { get; set; }
        public List<int> ObservedConsecutiveFailures { get; } = [];
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowNextIteration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private sealed class FakeDistributedLock : IDistributedLock
    {
        private readonly bool _returnHandle;

        public FakeDistributedLock(bool returnHandle)
        {
            _returnHandle = returnHandle;
        }

        public int TryAcquireCount { get; private set; }

        public Task<IDistributedLockHandle?> TryAcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
        {
            TryAcquireCount++;
            return Task.FromResult<IDistributedLockHandle?>(
                _returnHandle ? new FakeDistributedLockHandle(name) : null);
        }

        public Task<IDistributedLockHandle> AcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IDistributedLockHandle>(new FakeDistributedLockHandle(name));
        }
    }

    private sealed class FakeDistributedLockHandle : IDistributedLockHandle
    {
        public FakeDistributedLockHandle(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
