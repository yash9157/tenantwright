using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EventBus.Messaging;

namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>
/// Deletes inbox records older than <see cref="MessagingTrustOptions.MaximumOriginAge"/> plus
/// <see cref="TenantWrightInboxOptions.RetentionBeyondOriginAge"/>, in bounded batches. Any redelivery older than that
/// window is already rejected as stale by the inbound verifier, so deduplication stays complete.
/// </summary>
internal sealed class TenantWrightInboxRetentionService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    TenantDatabaseFanOut fanOut,
    IOptions<TenantWrightInboxOptions> inboxOptions,
    IOptions<MessagingOptions> messagingOptions,
    TimeProvider timeProvider,
    ILogger<TenantWrightInboxRetentionService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Inbox retention sweep failed with {ErrorType}; retrying at the next interval.",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(inboxOptions.Value.CleanupInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sweeps every database of <typeparamref name="TDbContext"/>: a tenant-scoped message is deduplicated in the
    /// inbox of the database its tenant resolves to. Returns the total deleted.
    /// </summary>
    internal async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var deleted = await fanOut.ForEachDatabaseAsync<TDbContext, int>(
            "inbox-retention",
            (_, token) => DeleteExpiredInDatabaseAsync(token),
            cancellationToken);
        return deleted.Sum();
    }

    private async Task<int> DeleteExpiredInDatabaseAsync(CancellationToken cancellationToken)
    {
        var options = inboxOptions.Value;
        var cutoff = timeProvider.GetUtcNow() -
                     messagingOptions.Value.Trust.MaximumOriginAge -
                     options.RetentionBeyondOriginAge;

        await using var scope = scopeFactory.CreateAsyncScope();
        await using var lease = await TenantDatabaseDbContextActivator.CreateForCurrentScopeAsync<TDbContext>(
            scope.ServiceProvider);
        var store = new TenantWrightMessagingStore(lease.DbContext);
        var total = 0;
        int deleted;
        do
        {
            deleted = await store.DeleteInboxRecordsBeforeAsync(cutoff, options.CleanupBatchSize, cancellationToken);
            total += deleted;
        }
        while (deleted == options.CleanupBatchSize && !cancellationToken.IsCancellationRequested);
        return total;
    }
}
