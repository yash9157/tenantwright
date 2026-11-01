using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;

namespace Shared.BackgroundTasks.Workers;

internal sealed class SharedPeriodicBackgroundService<TWorker, TOptions> : BackgroundService
    where TWorker : class, IPeriodicBackgroundWorker
    where TOptions : BackgroundWorkerOptions, new()
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock _distributedLock;
    private readonly IOptionsMonitor<TOptions> _optionsMonitor;
    private readonly ILogger<SharedPeriodicBackgroundService<TWorker, TOptions>> _logger;
    private int _consecutiveFailures;

    public SharedPeriodicBackgroundService(
        IServiceScopeFactory scopeFactory,
        IDistributedLock distributedLock,
        IOptionsMonitor<TOptions> optionsMonitor,
        ILogger<SharedPeriodicBackgroundService<TWorker, TOptions>> logger)
    {
        _scopeFactory = scopeFactory;
        _distributedLock = distributedLock;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue;
            BackgroundWorkerOptionsPostConfigure.Normalize(options);
            var delayHandled = false;

            try
            {
                if (options.Enabled)
                {
                    var result = await ExecuteIterationAsync(options, stoppingToken);
                    _consecutiveFailures = 0;
                    delayHandled = result.DelayHandled;
                    if (result.RunAgainImmediately)
                    {
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(
                    ex,
                    "Background worker {WorkerName} iteration failed. ConsecutiveFailures={ConsecutiveFailures}.",
                    options.WorkerName,
                    _consecutiveFailures);
            }

            if (!delayHandled)
            {
                await DelayAsync(options, stoppingToken);
            }
        }
    }

    private async Task<BackgroundWorkerIterationResult> ExecuteIterationAsync(
        TOptions options,
        CancellationToken stoppingToken)
    {
        if (options.DistributedLockName == null)
        {
            return await ExecuteWorkerAsync(options, stoppingToken);
        }

        await using var ownerLock = await _distributedLock.TryAcquireAsync(
            options.DistributedLockName,
            new DistributedLockAcquireOptions
            {
                AcquireTimeoutMs = options.OwnershipAcquireTimeoutMs,
                LeaseMs = options.OwnershipLeaseMs,
                IgnoreTenant = true
            },
            stoppingToken);

        if (ownerLock != null)
        {
            return await ExecuteWorkerAsync(options, stoppingToken);
        }

        BackgroundTasksTelemetry.RecordWorkerLockSkipped(options.WorkerName!, options.DistributedLockName);
        return BackgroundWorkerIterationResult.DefaultDelay;
    }

    private async Task<BackgroundWorkerIterationResult> ExecuteWorkerAsync(
        TOptions options,
        CancellationToken stoppingToken)
    {
        var startedAt = BackgroundTasksTelemetry.StartTimestamp();
        var succeeded = false;
        using var scope = _scopeFactory.CreateScope();
        var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var unitOfWork = unitOfWorkManager.Begin(new UnitOfWorkOptions { IsTransactional = false });
        foreach (var initializer in scope.ServiceProvider.GetServices<IPersistenceBoundaryInitializer>())
        {
            await initializer.InitializeAsync(stoppingToken);
        }

        var worker = scope.ServiceProvider.GetRequiredService<TWorker>();
        var context = new BackgroundWorkerContext(
            scope.ServiceProvider,
            options.WorkerName!,
            _consecutiveFailures,
            DateTime.UtcNow,
            stoppingToken);

        try
        {
            var result = await worker.ExecuteAsync(context);
            await unitOfWork.CompleteAsync(stoppingToken);
            succeeded = true;
            BackgroundTasksTelemetry.RecordWorkerIteration(options.WorkerName!, startedAt, succeeded);

            if (result.RunAgainImmediately)
            {
                return BackgroundWorkerIterationResult.ImmediateRepeat;
            }

            if (result.WaitAsync != null)
            {
                await result.WaitAsync(stoppingToken);
                return BackgroundWorkerIterationResult.CustomDelayHandled;
            }

            return BackgroundWorkerIterationResult.DefaultDelay;
        }
        finally
        {
            if (!succeeded)
            {
                BackgroundTasksTelemetry.RecordWorkerIteration(options.WorkerName!, startedAt, succeeded);
            }
        }
    }

    private async Task DelayAsync(TOptions options, CancellationToken stoppingToken)
    {
        var delayMs = _consecutiveFailures > 0
            ? Math.Min(options.ErrorDelayMs * _consecutiveFailures, 300_000)
            : options.PollIntervalMs;

        if (options.JitterMs > 0)
        {
            delayMs += Random.Shared.Next(0, options.JitterMs + 1);
        }

        BackgroundTasksTelemetry.RecordWorkerDelay(options.WorkerName!, delayMs, _consecutiveFailures > 0);
        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), stoppingToken);
    }

    private readonly record struct BackgroundWorkerIterationResult(
        bool RunAgainImmediately,
        bool DelayHandled)
    {
        public static BackgroundWorkerIterationResult DefaultDelay { get; } = new(false, false);

        public static BackgroundWorkerIterationResult CustomDelayHandled { get; } = new(false, true);

        public static BackgroundWorkerIterationResult ImmediateRepeat { get; } = new(true, true);
    }
}
