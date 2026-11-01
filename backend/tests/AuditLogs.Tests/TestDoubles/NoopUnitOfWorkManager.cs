using Microsoft.Extensions.DependencyInjection;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;

namespace AuditLogs.Tests.TestDoubles;

internal sealed class NoopUnitOfWorkManager : IUnitOfWorkManager
{
    public IUnitOfWork? Current => null;
    public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => new NoopUnitOfWork(options);
    public IUnitOfWork Begin(bool requiresNew = false) => new NoopUnitOfWork(new UnitOfWorkOptions());
    public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => new NoopUnitOfWork(new UnitOfWorkOptions());
    public void BeginReserved(string reservationName, UnitOfWorkOptions options) { }
    public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => true;
}

internal sealed class NoopUnitOfWork : IUnitOfWork
{
    public NoopUnitOfWork(UnitOfWorkOptions options)
    {
        Options = options;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public event EventHandler<UnitOfWorkFailedEventArgs>? Failed { add { } remove { } }
    public event EventHandler<UnitOfWorkEventArgs>? Disposed;
    public UnitOfWorkOptions Options { get; }
    public IUnitOfWork? Outer => null;
    public bool IsReserved => false;
    public bool IsDisposed { get; private set; }
    public bool IsCompleted { get; private set; }
    public string? ReservationName => null;
    public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

    public void SetOuter(IUnitOfWork? outer) { }
    public void Initialize(UnitOfWorkOptions options) { }
    public void Reserve(string reservationName) { }
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        IsCompleted = true;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void OnCompleted(Func<Task> handler) { }
    public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => factory(key);
    public IDatabaseApi? FindDatabaseApi(string key) => null;
    public void AddDatabaseApi(string key, IDatabaseApi api) { }
    public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();
    public ITransactionApi? FindTransactionApi(string key) => null;
    public void AddTransactionApi(string key, ITransactionApi api) { }
    public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();
    public void Dispose()
    {
        IsDisposed = true;
        Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
    }
}
