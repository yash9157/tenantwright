using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;

namespace Shared.Uow.Implementations;

public class ChildUnitOfWork : IUnitOfWork
{
    public Guid Id => _parent.Id;

    public IDictionary<string, object?> Items => _parent.Items;

    public event EventHandler<UnitOfWorkFailedEventArgs> Failed = default!;
    public event EventHandler<UnitOfWorkEventArgs> Disposed = default!;

    public UnitOfWorkOptions Options => _parent.Options;

    public IServiceProvider ServiceProvider => _parent.ServiceProvider;

    public IUnitOfWork? Outer => _parent.Outer;

    public bool IsReserved => _parent.IsReserved;

    public bool IsDisposed => _parent.IsDisposed;

    public bool IsCompleted => _parent.IsCompleted;

    public string? ReservationName => _parent.ReservationName;

    private readonly IUnitOfWork _parent;
    private readonly EventHandler<UnitOfWorkFailedEventArgs> _failedHandler;
    private readonly EventHandler<UnitOfWorkEventArgs> _disposedHandler;
    private int _disposed;

    public ChildUnitOfWork(IUnitOfWork parent)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));

        _failedHandler = (sender, args) => Failed?.Invoke(sender!, args);
        _disposedHandler = (sender, args) => Disposed?.Invoke(sender!, args);
        _parent.Failed += _failedHandler;
        _parent.Disposed += _disposedHandler;
    }

    public void SetOuter(IUnitOfWork? outer)
    {
        _parent.SetOuter(outer);
    }

    public void Initialize(UnitOfWorkOptions options)
    {
        _parent.Initialize(options);
    }

    public void Reserve(string reservationName)
    {
        _parent.Reserve(reservationName);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _parent.SaveChangesAsync(cancellationToken);
    }

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        return _parent.RollbackAsync(cancellationToken);
    }

    public IDatabaseApi? FindDatabaseApi(string key)
    {
        return _parent.FindDatabaseApi(key);
    }

    public void AddDatabaseApi(string key, IDatabaseApi api)
    {
        _parent.AddDatabaseApi(key, api);
    }

    public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory)
    {
        return _parent.GetOrAddDatabaseApi(key, factory);
    }

    public ITransactionApi? FindTransactionApi(string key)
    {
        return _parent.FindTransactionApi(key);
    }

    public void AddTransactionApi(string key, ITransactionApi api)
    {
        _parent.AddTransactionApi(key, api);
    }

    public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory)
    {
        return _parent.GetOrAddTransactionApi(key, factory);
    }

    public void OnCompleted(Func<Task> handler)
    {
        _parent.OnCompleted(handler);
    }

    public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory)
    {
        return _parent.GetOrAddItem(key, factory);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _parent.Failed -= _failedHandler;
        _parent.Disposed -= _disposedHandler;
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        Dispose();
        return ValueTask.CompletedTask;
    }

    public override string ToString()
    {
        return $"[UnitOfWork {Id}]";
    }
}
