using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EventBus.Messaging;

namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>
/// Delivers this host's outbox through the active <see cref="ITenantWrightMessagingTransport"/>. Safe with any number of
/// replicas: each claim leases a disjoint batch with <c>FOR UPDATE SKIP LOCKED</c>, and a row is marked dispatched only
/// after the transport reports broker confirmation. Delivery is at least once; consumers deduplicate in their inbox.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher never re-signs custody. The origin attestation was signed when the producer wrote the row, and the
/// consumer requires the broker identity to equal that origin producer, which this host is. A row whose origin would
/// already be stale at the consumer is parked rather than sent to certain rejection.
/// </para>
/// <para>
/// A failed dispatch is retried with exponential backoff and jitter, and parked after
/// <see cref="TenantWrightOutboxOptions.MaxAttempts"/>. Parked rows are kept for an operator; only dispatched rows are
/// deleted by retention.
/// </para>
/// </remarks>
internal sealed class TenantWrightOutboxDispatcher<TDbContext>(
    IServiceScopeFactory scopeFactory,
    TenantDatabaseFanOut fanOut,
    ITenantWrightMessagingTransport transport,
    TenantWrightOutboxDispatchSignal dispatchSignal,
    IOptions<TenantWrightOutboxOptions> outboxOptions,
    IOptions<MessagingOptions> messagingOptions,
    TimeProvider timeProvider,
    ILogger<TenantWrightOutboxDispatcher<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    internal const string OriginExpiredReason = "OriginExpired";
    internal const string AttemptsExhaustedReason = "AttemptsExhausted";

    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = outboxOptions.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await DispatchBatchAsync(stoppingToken);
                if (timeProvider.GetUtcNow() >= _nextCleanupAt)
                {
                    await DeleteExpiredDispatchedAsync(stoppingToken);
                    _nextCleanupAt = timeProvider.GetUtcNow() + options.CleanupInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Database or lease failures: no row state was changed outside a fenced statement, so the next cycle
                // retries safely. Exception type only; provider messages can echo parameter values.
                logger.LogWarning(
                    "Outbox dispatch cycle failed with {ErrorType}; retrying after the poll interval.",
                    exception.GetType().Name);
            }

            if (claimed >= options.BatchSize)
                continue;

            try
            {
                await dispatchSignal.WaitAsync(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Claims and dispatches one batch from every database of <typeparamref name="TDbContext"/>: a message written in
    /// a dedicated tenant's scope lives in that tenant's database and is dispatched from there, in that tenant's scope.
    /// Returns the largest number of rows claimed from a single database, so a full batch anywhere loops immediately.
    /// </summary>
    internal async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var claimed = await fanOut.ForEachDatabaseAsync<TDbContext, int>(
            "outbox-dispatch",
            (_, token) => DispatchDatabaseBatchAsync(token),
            cancellationToken);
        return claimed.Count == 0 ? 0 : claimed.Max();
    }

    private async Task<int> DispatchDatabaseBatchAsync(CancellationToken cancellationToken)
    {
        var options = outboxOptions.Value;
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var lease = await TenantDatabaseDbContextActivator.CreateForCurrentScopeAsync<TDbContext>(
            scope.ServiceProvider);
        var store = new TenantWrightMessagingStore(lease.DbContext);

        var now = timeProvider.GetUtcNow();
        var leaseUntil = now + options.LeaseDuration;
        var leaseOwner = $"{_instanceId}:{Guid.NewGuid():N}";
        if (leaseOwner.Length > TenantWrightMessagingTables.LockOwnerMaxLength)
            leaseOwner = leaseOwner[^TenantWrightMessagingTables.LockOwnerMaxLength..];

        var claimed = await store.ClaimAsync(now, leaseUntil, leaseOwner, options.BatchSize, cancellationToken);
        for (var index = 0; index < claimed.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested ||
                timeProvider.GetUtcNow() + options.DispatchTimeout > leaseUntil)
            {
                // Never dispatch on a lease that could expire mid-send: another dispatcher may already own the row.
                await store.ReleaseUnattemptedAsync(
                    claimed.Skip(index).Select(message => message.Id).ToArray(),
                    leaseOwner,
                    CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                break;
            }

            await DispatchOneAsync(store, claimed[index], leaseOwner, options, cancellationToken);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Deletes dispatched rows older than the retention window in bounded batches, in every database of
    /// <typeparamref name="TDbContext"/>. Returns the total deleted.
    /// </summary>
    internal async Task<int> DeleteExpiredDispatchedAsync(CancellationToken cancellationToken)
    {
        var deleted = await fanOut.ForEachDatabaseAsync<TDbContext, int>(
            "outbox-retention",
            (_, token) => DeleteExpiredDispatchedInDatabaseAsync(token),
            cancellationToken);
        return deleted.Sum();
    }

    private async Task<int> DeleteExpiredDispatchedInDatabaseAsync(CancellationToken cancellationToken)
    {
        var options = outboxOptions.Value;
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var lease = await TenantDatabaseDbContextActivator.CreateForCurrentScopeAsync<TDbContext>(
            scope.ServiceProvider);
        var store = new TenantWrightMessagingStore(lease.DbContext);
        var cutoff = timeProvider.GetUtcNow() - options.DispatchedRetention;

        var total = 0;
        int deleted;
        do
        {
            deleted = await store.DeleteDispatchedBeforeAsync(cutoff, options.CleanupBatchSize, cancellationToken);
            total += deleted;
        }
        while (deleted == options.CleanupBatchSize && !cancellationToken.IsCancellationRequested);
        return total;
    }

    private async Task DispatchOneAsync(
        TenantWrightMessagingStore store,
        ClaimedOutboxMessage message,
        string leaseOwner,
        TenantWrightOutboxOptions options,
        CancellationToken cancellationToken)
    {
        if (IsOriginStale(message))
        {
            await ParkAsync(store, message, leaseOwner, OriginExpiredReason, OriginExpiredReason);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.DispatchTimeout);
        try
        {
            await transport.DispatchAsync(
                new TenantWrightTransportMessage(
                    message.Id,
                    message.ContractIdentity,
                    message.Kind,
                    message.Destination,
                    message.Payload,
                    message.Headers),
                timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-dispatch: the broker outcome is unknown, so the attempt stands and the row is retried.
            await ScheduleRetryAsync(store, message, leaseOwner, nameof(OperationCanceledException), options);
            throw;
        }
        catch (Exception exception)
        {
            var errorType = exception.GetType().Name;
            TenantWrightMessagingMetrics.RecordDispatchFailed(message.ContractIdentity, errorType);
            if (message.Attempts >= options.MaxAttempts)
            {
                await ParkAsync(store, message, leaseOwner, errorType, AttemptsExhaustedReason);
                return;
            }

            await ScheduleRetryAsync(store, message, leaseOwner, errorType, options);
            return;
        }

        var dispatchedAt = timeProvider.GetUtcNow();
        if (await store.MarkDispatchedAsync(message.Id, leaseOwner, dispatchedAt, CancellationToken.None))
        {
            TenantWrightMessagingMetrics.RecordDispatched(message.ContractIdentity, dispatchedAt - message.CreatedAtUtc);
            return;
        }

        // The lease was lost after the broker accepted the message; the new owner redelivers and consumers deduplicate.
        logger.LogWarning(
            "Outbox message {MessageId} was dispatched after its lease was lost; a duplicate delivery may follow.",
            message.Id);
    }

    private bool IsOriginStale(ClaimedOutboxMessage message)
    {
        var trust = messagingOptions.Value.Trust;
        if (!message.Headers.TryGetValue(TenantWrightTrustHeaders.OriginIssuedAt, out var issuedAtText) ||
            !DateTimeOffset.TryParseExact(
                issuedAtText,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var issuedAt))
            return true;

        // Consumers reject an origin older than MaximumOriginAge; their clocks may run up to the skew ahead of ours.
        return issuedAt + trust.MaximumOriginAge <= timeProvider.GetUtcNow() + trust.MaximumFutureClockSkew;
    }

    private async Task ScheduleRetryAsync(
        TenantWrightMessagingStore store,
        ClaimedOutboxMessage message,
        string leaseOwner,
        string errorType,
        TenantWrightOutboxOptions options)
    {
        var availableAt = timeProvider.GetUtcNow() + RetryDelay(message.Attempts, options);
        await store.ScheduleRetryAsync(message.Id, leaseOwner, availableAt, errorType, CancellationToken.None);
    }

    private async Task ParkAsync(
        TenantWrightMessagingStore store,
        ClaimedOutboxMessage message,
        string leaseOwner,
        string errorType,
        string reason)
    {
        if (!await store.ParkAsync(message.Id, leaseOwner, timeProvider.GetUtcNow(), errorType, CancellationToken.None))
            return;

        TenantWrightMessagingMetrics.RecordParked(message.ContractIdentity, reason);
        logger.LogWarning(
            "Parked outbox message {MessageId} ({Contract}) after {Attempts} attempts: {Reason}, last error {ErrorType}.",
            message.Id,
            message.ContractIdentity,
            message.Attempts,
            reason,
            errorType);
    }

    /// <summary>Exponential backoff from the attempt just made, capped, with equal jitter in [delay/2, delay].</summary>
    internal static TimeSpan RetryDelay(int attempts, TenantWrightOutboxOptions options)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var ceiling = Math.Min(
            options.RetryMaxDelay.TotalMilliseconds,
            options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, exponent));
        var jittered = ceiling / 2 + Random.Shared.NextDouble() * ceiling / 2;
        return TimeSpan.FromMilliseconds(jittered);
    }
}
