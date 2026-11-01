using Shared.Uow.Options;
using Shared.Uow.Events;

namespace Shared.Uow.Abstractions;

public interface IUnitOfWork : IDatabaseApiContainer, ITransactionApiContainer, IDisposable, IAsyncDisposable
{
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    Guid Id { get; }

    IDictionary<string, object?> Items { get; }

    event EventHandler<UnitOfWorkFailedEventArgs> Failed;

    event EventHandler<UnitOfWorkEventArgs> Disposed;

    UnitOfWorkOptions Options { get; }

    IUnitOfWork? Outer { get; }

    bool IsReserved { get; }

    bool IsDisposed { get; }

    bool IsCompleted { get; }

    string? ReservationName { get; }

    void SetOuter(IUnitOfWork? outer);

    void Initialize(UnitOfWorkOptions options);

    void Reserve(string reservationName);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    Task CompleteAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);

    void OnCompleted(Func<Task> handler);

    TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory);
}
