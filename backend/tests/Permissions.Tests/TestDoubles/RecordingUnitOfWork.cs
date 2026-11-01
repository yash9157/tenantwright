using Microsoft.Extensions.DependencyInjection;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;

namespace Permissions.Tests.TestDoubles;

/// <summary>
/// Deterministic ambient UoW for tests that must observe post-commit callbacks.
/// It deliberately executes completed handlers in registration order and lets a handler failure escape.
/// </summary>
internal sealed class RecordingUnitOfWorkManager : IUnitOfWorkManager
{
    public RecordingUnitOfWork UnitOfWork { get; } = new();

    public IUnitOfWork? Current => UnitOfWork;

    public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => UnitOfWork;
    public IUnitOfWork Begin(bool requiresNew = false) => UnitOfWork;
    public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => UnitOfWork;
    public void BeginReserved(string reservationName, UnitOfWorkOptions options) { }
    public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => true;
}

internal sealed class RecordingUnitOfWork : IUnitOfWork
{
    private readonly List<Func<Task>> _completedHandlers = [];

    public Guid Id { get; } = Guid.CreateVersion7();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
    public event EventHandler<UnitOfWorkFailedEventArgs>? Failed { add { } remove { } }
    public event EventHandler<UnitOfWorkEventArgs>? Disposed { add { } remove { } }
    public UnitOfWorkOptions Options { get; } = new();
    public IUnitOfWork? Outer => null;
    public bool IsReserved => false;
    public bool IsDisposed => false;
    public bool IsCompleted { get; private set; }
    public string? ReservationName => null;
    public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();
    public int RegisteredCompletionHandlerCount => _completedHandlers.Count;

    public void SetOuter(IUnitOfWork? outer) { }
    public void Initialize(UnitOfWorkOptions options) { }
    public void Reserve(string reservationName) { }
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        IsCompleted = true;
        foreach (var handler in _completedHandlers.ToArray())
            await handler();
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void OnCompleted(Func<Task> handler) => _completedHandlers.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
    public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => Items.TryGetValue(key, out var value) && value is TItem item ? item : (TItem)(Items[key] = factory(key))!;
    public IDatabaseApi? FindDatabaseApi(string key) => null;
    public void AddDatabaseApi(string key, IDatabaseApi api) { }
    public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();
    public ITransactionApi? FindTransactionApi(string key) => null;
    public void AddTransactionApi(string key, ITransactionApi api) { }
    public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();
    public void Dispose() { }
}
