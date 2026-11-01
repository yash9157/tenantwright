using Microsoft.Extensions.Options;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;
using Shared.Modularity.DependencyInjection;

namespace Shared.Uow.Implementations;

public class UnitOfWork : IUnitOfWork, IUnitOfWorkScopeOwner, ITransientDependency
{
    public const string UnitOfWorkReservationName = "_ActionUnitOfWork";

    public Guid Id { get; } = Guid.NewGuid();

    public IDictionary<string, object?> Items { get; }

    public event EventHandler<UnitOfWorkFailedEventArgs> Failed = default!;
    public event EventHandler<UnitOfWorkEventArgs> Disposed = default!;

    public UnitOfWorkOptions Options { get; private set; } = default!;

    public IUnitOfWork? Outer { get; private set; }

    public bool IsReserved { get; private set; }

    public bool IsDisposed { get; private set; }

    public bool IsCompleted { get; private set; }

    public string? ReservationName { get; private set; }

    protected List<Func<Task>> CompletedHandlers { get; } = [];

    public IServiceProvider ServiceProvider { get; }
    private readonly Dictionary<string, IDatabaseApi> _databaseApis = [];
    private readonly Dictionary<string, ITransactionApi> _transactionApis = [];
    private readonly UnitOfWorkDefaultOptions _defaultOptions;

    private static readonly AsyncLocal<UnitOfWork?> ScopeDisposalOwner = new();

    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _exception;
    private bool _isCompleting;
    private bool _isRolledback;
    private Action? _restoreAmbient;
    private Func<ValueTask>? _disposeScopeAsync;
    private int _disposeStarted;

    public UnitOfWork(
        IOptions<UnitOfWorkDefaultOptions> options,
        IServiceProvider serviceProvider)
    {
        _defaultOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        Items = new Dictionary<string, object?>();
    }

    void IUnitOfWorkScopeOwner.SetScopeLifetime(Action restoreAmbient, Func<ValueTask> disposeScopeAsync)
    {
        ArgumentNullException.ThrowIfNull(restoreAmbient);
        ArgumentNullException.ThrowIfNull(disposeScopeAsync);

        if (_restoreAmbient is not null || _disposeScopeAsync is not null)
        {
            throw new InvalidOperationException("The unit of work scope lifetime has already been assigned.");
        }

        _restoreAmbient = restoreAmbient;
        _disposeScopeAsync = disposeScopeAsync;
    }

    public virtual void Initialize(UnitOfWorkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (Options != null)
        {
            throw new InvalidOperationException("This unit of work has already been initialized.");
        }

        Options = _defaultOptions.Normalize(options.Clone());
        IsReserved = false;
    }

    public virtual void Reserve(string reservationName)
    {
        if (string.IsNullOrWhiteSpace(reservationName))
        {
            throw new ArgumentException("Reservation name cannot be null or whitespace.", nameof(reservationName));
        }

        ReservationName = reservationName;
        IsReserved = true;
    }

    public virtual void SetOuter(IUnitOfWork? outer)
    {
        Outer = outer;
    }

    public virtual async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_isRolledback)
        {
            return;
        }

        foreach (var databaseApi in GetAllActiveDatabaseApis())
        {
            if (databaseApi is ISupportsSavingChanges supportsSavingChanges)
            {
                await supportsSavingChanges.SaveChangesAsync(cancellationToken);
            }
        }
    }

    public virtual async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_isRolledback)
        {
            return;
        }

        PreventMultipleComplete();

        try
        {
            _isCompleting = true;
            await SaveChangesAsync(cancellationToken);

            await CommitTransactionsAsync(cancellationToken);
            IsCompleted = true;
            await OnCompletedAsync();
        }
        catch (Exception ex)
        {
            _exception = ex;
            throw;
        }
    }

    public virtual async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_isRolledback)
        {
            return;
        }

        _isRolledback = true;
        await RollbackAllAsync(cancellationToken);
    }

    public virtual IDatabaseApi? FindDatabaseApi(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Database API key can not be null or whitespace.", nameof(key));
        }

        return _databaseApis.GetValueOrDefault(key);
    }

    public virtual void AddDatabaseApi(string key, IDatabaseApi api)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Database API key can not be null or whitespace.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(api);

        if (_databaseApis.ContainsKey(key))
        {
            throw new InvalidOperationException("This unit of work already contains a database API for the given key: " + key);
        }

        _databaseApis.Add(key, api);
    }

    public virtual IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Database API key can not be null or whitespace.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(factory);

        if (_databaseApis.TryGetValue(key, out var api))
        {
            return api;
        }

        api = factory();
        _databaseApis.Add(key, api);
        return api;
    }

    public virtual ITransactionApi? FindTransactionApi(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Transaction API key can not be null or whitespace.", nameof(key));
        }

        return _transactionApis.GetValueOrDefault(key);
    }

    public virtual void AddTransactionApi(string key, ITransactionApi api)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Transaction API key can not be null or whitespace.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(api);

        if (_transactionApis.ContainsKey(key))
        {
            throw new InvalidOperationException("This unit of work already contains a transaction API for the given key: " + key);
        }

        _transactionApis.Add(key, api);
    }

    public virtual ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Transaction API key can not be null or whitespace.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(factory);

        if (_transactionApis.TryGetValue(key, out var api))
        {
            return api;
        }

        api = factory();
        _transactionApis.Add(key, api);
        return api;
    }

    public virtual void OnCompleted(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        CompletedHandlers.Add(handler);
    }

    public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Item key can not be null or whitespace.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(factory);

        if (Items.TryGetValue(key, out var existing) && existing is TItem typedExisting)
        {
            return typedExisting;
        }

        var created = factory(key);
        Items[key] = created;
        return created;
    }

    protected virtual async Task OnCompletedAsync()
    {
        foreach (var handler in CompletedHandlers.ToArray())
        {
            await handler.Invoke();
        }
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);

        if (!TryBeginDispose())
        {
            WaitForDisposalIfRequired();
            return;
        }

        var previousScopeDisposalOwner = ScopeDisposalOwner.Value;
        ScopeDisposalOwner.Value = this;
        try
        {
            var disposeScopeAsync = PrepareDisposal();
            if (disposeScopeAsync is not null)
            {
                disposeScopeAsync().AsTask().GetAwaiter().GetResult();
            }

            Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
        }
        finally
        {
            ScopeDisposalOwner.Value = previousScopeDisposalOwner;
        }

        _disposeCompletion.Task.GetAwaiter().GetResult();
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (!TryBeginDispose())
        {
            return ReferenceEquals(ScopeDisposalOwner.Value, this)
                ? ValueTask.CompletedTask
                : new ValueTask(_disposeCompletion.Task);
        }

        Func<ValueTask>? disposeScopeAsync;
        try
        {
            disposeScopeAsync = PrepareDisposal();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            return new ValueTask(_disposeCompletion.Task);
        }

        return DisposeScopeAndNotifyAsync(disposeScopeAsync);
    }

    private bool TryBeginDispose()
    {
        return Interlocked.Exchange(ref _disposeStarted, 1) == 0;
    }

    private Func<ValueTask>? PrepareDisposal()
    {
        IsDisposed = true;

        foreach (var transactionApi in GetAllActiveTransactionApis())
        {
            try
            {
                transactionApi.Dispose();
            }
            catch
            {
                // Ignore disposal exceptions and keep disposing remaining transaction APIs.
            }
        }

        if (!IsCompleted || _exception != null)
        {
            Failed?.Invoke(this, new UnitOfWorkFailedEventArgs(this, _exception, _isRolledback));
        }

        _restoreAmbient?.Invoke();
        return _disposeScopeAsync;
    }

    private async ValueTask DisposeScopeAndNotifyAsync(Func<ValueTask>? disposeScopeAsync)
    {
        var previousScopeDisposalOwner = ScopeDisposalOwner.Value;
        ScopeDisposalOwner.Value = this;
        try
        {
            if (disposeScopeAsync is not null)
            {
                await disposeScopeAsync().ConfigureAwait(false);
            }

            Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
        }
        finally
        {
            ScopeDisposalOwner.Value = previousScopeDisposalOwner;
        }

        await _disposeCompletion.Task.ConfigureAwait(false);
    }

    private void WaitForDisposalIfRequired()
    {
        if (!ReferenceEquals(ScopeDisposalOwner.Value, this))
        {
            _disposeCompletion.Task.GetAwaiter().GetResult();
        }
    }

    protected virtual async Task RollbackAllAsync(CancellationToken cancellationToken)
    {
        foreach (var databaseApi in GetAllActiveDatabaseApis())
        {
            if (databaseApi is ISupportsRollback supportsRollback)
            {
                try
                {
                    await supportsRollback.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Ignore rollback exception and continue best-effort rollback.
                }
            }
        }

        foreach (var transactionApi in GetAllActiveTransactionApis())
        {
            try
            {
                await transactionApi.RollbackAsync(cancellationToken);
            }
            catch
            {
                // Ignore rollback exception and continue best-effort rollback.
            }
        }
    }

    protected virtual async Task CommitTransactionsAsync(CancellationToken cancellationToken)
    {
        foreach (var transaction in GetAllActiveTransactionApis())
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    protected virtual IReadOnlyList<IDatabaseApi> GetAllActiveDatabaseApis()
    {
        return _databaseApis.Values.ToArray();
    }

    protected virtual IReadOnlyList<ITransactionApi> GetAllActiveTransactionApis()
    {
        return _transactionApis.Values.ToArray();
    }

    private void PreventMultipleComplete()
    {
        if (IsCompleted || _isCompleting)
        {
            throw new InvalidOperationException("Completion has already been requested for this unit of work.");
        }
    }

    public override string ToString()
    {
        return $"[UnitOfWork {Id}]";
    }
}
