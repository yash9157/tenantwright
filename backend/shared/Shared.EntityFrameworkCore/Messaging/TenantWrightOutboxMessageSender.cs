using Microsoft.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;

namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>
/// The <see cref="ITenantWrightMessageSender"/> of every persistence-backed host. Stamps the message and writes it to the
/// outbox through the <typeparamref name="TDbContext"/> the current unit of work owns, so the message commits or rolls
/// back with the caller's business changes. Commands and events take the same durable path.
/// </summary>
/// <remarks>
/// <see cref="IDbContextProvider{TDbContext}"/> resolves the context from the ambient unit of work rather than from
/// the scope that resolved this sender, which is the property that keeps an outbox row out of an unsaved context. With
/// no ambient unit of work, the sender opens a short transactional unit of work of its own; it never writes outside
/// one. After the owning unit of work commits, the dispatcher is woken in-process.
/// </remarks>
internal sealed class TenantWrightOutboxMessageSender<TDbContext>(
    TenantWrightOutboundMessageStamper stamper,
    TenantWrightMessageCatalog catalog,
    IUnitOfWorkManager unitOfWorkManager,
    IDbContextProvider<TDbContext> dbContextProvider,
    TenantWrightOutboxDispatchSignal dispatchSignal,
    TimeProvider timeProvider) : ITenantWrightMessageSender
    where TDbContext : DbContext, IEfCoreDbContext
{
    private const string WakeUpRegisteredItem = "Shared.EntityFrameworkCore.Messaging.OutboxWakeUpRegistered";

    public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(command);
        var route = catalog.GetRoute(command.GetType());
        if (route.Kind != TenantWrightMessageRouteKind.Command || route.CommandDestination is null)
            throw new InvalidOperationException("The contract is not registered as an explicit command route.");
        return WriteAsync(stamper.Stamp(command, TenantWrightMessageRouteKind.Command), cancellationToken);
    }

    public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        var route = catalog.GetRoute(message.GetType());
        if (route.Kind != TenantWrightMessageRouteKind.Event)
            throw new InvalidOperationException("The contract is not registered as a published event.");
        return WriteAsync(stamper.Stamp(message, TenantWrightMessageRouteKind.Event), cancellationToken);
    }

    private async Task WriteAsync(TenantWrightOutboundMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = MessagingOutboxMessage.Create(message, timeProvider.GetUtcNow());

        var ambient = unitOfWorkManager.Current;
        if (ambient is not null)
        {
            await AddToUnitOfWorkAsync(ambient, row);
            return;
        }

        using var unitOfWork = unitOfWorkManager.Begin(
            new UnitOfWorkOptions { IsTransactional = true },
            requiresNew: true);
        await AddToUnitOfWorkAsync(unitOfWork, row);
        await unitOfWork.CompleteAsync(cancellationToken);
    }

    private async Task AddToUnitOfWorkAsync(IUnitOfWork unitOfWork, MessagingOutboxMessage row)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync();
        dbContext.Set<MessagingOutboxMessage>().Add(row);
        unitOfWork.GetOrAddItem(WakeUpRegisteredItem, _ =>
        {
            unitOfWork.OnCompleted(() =>
            {
                dispatchSignal.Notify();
                return Task.CompletedTask;
            });
            return true;
        });
    }
}
