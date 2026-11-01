using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.EventBus.Messaging;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;

namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>
/// The EF implementation of <see cref="ITenantWrightInboxTransaction"/>: one new transactional unit of work that inserts the
/// inbox record and runs the handler. The insert is the deduplication point. A concurrent delivery of the same message
/// blocks on the uncommitted key and, once the first transaction commits, finds the conflict and is acknowledged without
/// running the handler; if the first rolls back, the second proceeds.
/// </summary>
internal sealed class TenantWrightInboxTransaction<TDbContext>(
    IUnitOfWorkManager unitOfWorkManager,
    TimeProvider timeProvider) : ITenantWrightInboxTransaction
    where TDbContext : DbContext, IEfCoreDbContext
{
    private const string Table = $"{TenantWrightMessagingTables.Schema}.\"{TenantWrightMessagingTables.Inbox}\"";

    public async Task<bool> TryProcessOnceAsync(
        Guid messageId,
        string endpoint,
        Func<IServiceProvider, CancellationToken, Task> handle,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(handle);

        using var unitOfWork = unitOfWorkManager.Begin(
            new UnitOfWorkOptions { IsTransactional = true },
            requiresNew: true);
        var dbContext = await unitOfWork.ServiceProvider
            .GetRequiredService<IDbContextProvider<TDbContext>>()
            .GetDbContextAsync();
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The inbox transaction requires a relational transaction.");

        var processedAt = timeProvider.GetUtcNow();
        var recorded = await dbContext.Database.ExecuteSqlRawAsync(
            $$"""
             INSERT INTO {{Table}} ("MessageId", "Endpoint", "ProcessedAtUtc")
             VALUES ({0}, {1}, {2})
             ON CONFLICT ("MessageId", "Endpoint") DO NOTHING
             """,
            [messageId, endpoint, processedAt],
            cancellationToken);
        if (recorded == 0)
        {
            await unitOfWork.CompleteAsync(cancellationToken);
            return false;
        }

        await handle(unitOfWork.ServiceProvider, cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);
        return true;
    }
}
