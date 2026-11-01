using Castle.DynamicProxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Attributes;
using Shared.Uow.Events;
using Shared.Uow.Implementations;
using Shared.Uow.Options;
using Xunit;

namespace Shared.Modularity.Tests;

public class UnitOfWorkExecutorTests
{
    [Fact]
    public async Task ExecuteTransactionalAsync_should_begin_fresh_transactional_scope_and_complete()
    {
        var manager = new RecordingUnitOfWorkManager();
        var executor = CreateExecutor(manager);
        var executed = false;

        await executor.ExecuteTransactionalAsync(_ =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        var unitOfWork = Assert.Single(manager.Started);
        Assert.True(executed);
        Assert.True(manager.LastRequiresNew);
        Assert.False(manager.TryBeginReservedCalled);
        Assert.True(unitOfWork.Options.IsTransactional);
        Assert.True(unitOfWork.IsCompleted);
        Assert.True(unitOfWork.IsDisposed);
    }

    [Fact]
    public async Task ExecuteNonTransactionalAsync_should_begin_fresh_non_transactional_scope()
    {
        var manager = new RecordingUnitOfWorkManager();
        var executor = CreateExecutor(manager);

        await executor.ExecuteNonTransactionalAsync(_ => Task.CompletedTask);

        var unitOfWork = Assert.Single(manager.Started);
        Assert.True(manager.LastRequiresNew);
        Assert.False(unitOfWork.Options.IsTransactional);
        Assert.True(unitOfWork.IsCompleted);
        Assert.True(unitOfWork.IsDisposed);
    }

    [Fact]
    public async Task ExecuteTransactionalAsync_should_propagate_operation_result()
    {
        var manager = new RecordingUnitOfWorkManager();
        var executor = CreateExecutor(manager);

        var result = await executor.ExecuteTransactionalAsync(_ => Task.FromResult(42));

        Assert.Equal(42, result);
        var unitOfWork = Assert.Single(manager.Started);
        Assert.True(unitOfWork.IsCompleted);
    }

    [Fact]
    public async Task ExecuteNonTransactionalAsync_should_propagate_operation_result()
    {
        var manager = new RecordingUnitOfWorkManager();
        var executor = CreateExecutor(manager);

        var result = await executor.ExecuteNonTransactionalAsync(_ => Task.FromResult("done"));

        Assert.Equal("done", result);
        Assert.False(Assert.Single(manager.Started).Options.IsTransactional);
    }

    [Fact]
    public async Task ExecuteTransactionalAsync_should_dispose_without_completing_on_failure()
    {
        var manager = new RecordingUnitOfWorkManager();
        var executor = CreateExecutor(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteTransactionalAsync(
                _ => throw new InvalidOperationException("boom")));

        var unitOfWork = Assert.Single(manager.Started);
        Assert.False(unitOfWork.IsCompleted);
        Assert.True(unitOfWork.IsDisposed);
    }

    [Fact]
    public async Task ExecuteTransactionalAsync_generic_should_dispose_without_completing_on_failure()
    {
        var manager = new RecordingUnitOfWorkManager();
        var executor = CreateExecutor(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteTransactionalAsync<int>(
                _ => throw new InvalidOperationException("boom")));

        var unitOfWork = Assert.Single(manager.Started);
        Assert.False(unitOfWork.IsCompleted);
        Assert.True(unitOfWork.IsDisposed);
    }

    [Fact]
    public async Task ExecuteTransactionalAsync_should_forward_cancellation_token_to_operation()
    {
        var manager = new RecordingUnitOfWorkManager();
        var executor = CreateExecutor(manager);
        using var cts = new CancellationTokenSource();
        CancellationToken observed = default;

        await executor.ExecuteTransactionalAsync(
            token =>
            {
                observed = token;
                return Task.CompletedTask;
            },
            cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public void Executor_should_be_exposed_only_through_its_interface()
    {
        var attribute = Assert.Single(
            typeof(UnitOfWorkExecutor).GetCustomAttributes(typeof(ExposeServicesAttribute), inherit: false)
                .Cast<ExposeServicesAttribute>());

        Assert.Equal([typeof(IUnitOfWorkExecutor)], attribute.ServiceTypes);
        Assert.False(attribute.IncludeSelf);
        Assert.False(attribute.IncludeDefaults);
        Assert.True(typeof(UnitOfWorkExecutor).IsSealed);
    }

    private static IUnitOfWorkExecutor CreateExecutor(RecordingUnitOfWorkManager manager)
    {
        var interceptor = new UnitOfWorkInterceptor(
            manager,
            new UnitOfWorkSuppressionContext(),
            Options.Create(new UnitOfWorkDefaultOptions()));

        return new ProxyGenerator()
            .CreateInterfaceProxyWithTarget<IUnitOfWorkExecutor>(new UnitOfWorkExecutor(), interceptor);
    }

    private sealed class RecordingUnitOfWorkManager : IUnitOfWorkManager
    {
        public List<RecordingUnitOfWork> Started { get; } = [];
        public bool LastRequiresNew { get; private set; }
        public bool TryBeginReservedCalled { get; private set; }
        public IUnitOfWork? Current => null;

        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false)
        {
            LastRequiresNew = requiresNew;
            var unitOfWork = new RecordingUnitOfWork(options);
            Started.Add(unitOfWork);
            return unitOfWork;
        }

        public IUnitOfWork Begin(bool requiresNew = false) => Begin(new UnitOfWorkOptions(), requiresNew);
        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => Begin(requiresNew);
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();

        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options)
        {
            TryBeginReservedCalled = true;
            return false;
        }
    }

    private sealed class RecordingUnitOfWork(UnitOfWorkOptions options) : IUnitOfWork
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public event EventHandler<UnitOfWorkFailedEventArgs>? Failed
        {
            add { }
            remove { }
        }
        public event EventHandler<UnitOfWorkEventArgs>? Disposed;
        public UnitOfWorkOptions Options { get; } = options;
        public IUnitOfWork? Outer => null;
        public bool IsReserved => false;
        public bool IsDisposed { get; private set; }
        public bool IsCompleted { get; private set; }
        public string? ReservationName => null;
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public void SetOuter(IUnitOfWork? outer) { }
        public void Initialize(UnitOfWorkOptions value) { }
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

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
