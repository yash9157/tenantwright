using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Uow.Abstractions;
using Shared.Uow.Context;
using Shared.Uow.Events;
using Shared.Uow.Implementations;
using Shared.Uow.Options;
using Castle.DynamicProxy;
using Shared.Uow.Attributes;
using Xunit;

namespace Shared.Modularity.Tests;

public class UnitOfWorkHardeningTests
{
    [Fact]
    public void AmbientUnitOfWork_GetCurrentByChecking_should_skip_reserved_disposed_and_completed()
    {
        var ambient = new AmbientUnitOfWork();
        var active = new StubUnitOfWork();
        var completed = new StubUnitOfWork { IsCompleted = true, Outer = active };
        var disposed = new StubUnitOfWork { IsDisposed = true, Outer = completed };
        var reserved = new StubUnitOfWork { IsReserved = true, Outer = disposed };

        ambient.SetUnitOfWork(reserved);

        Assert.Same(active, ambient.GetCurrentByChecking());
    }

    [Fact]
    public async Task UnitOfWork_CompleteAsync_should_throw_on_second_call()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var unitOfWork = new UnitOfWork(
            Options.Create(new UnitOfWorkDefaultOptions()),
            provider);

        unitOfWork.Initialize(new UnitOfWorkOptions { IsTransactional = true });
        await unitOfWork.CompleteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CompleteAsync());
    }

    [Fact]
    public async Task UnitOfWork_RollbackAsync_should_continue_when_a_transaction_throws()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var unitOfWork = new UnitOfWork(
            Options.Create(new UnitOfWorkDefaultOptions()),
            provider);
        unitOfWork.Initialize(new UnitOfWorkOptions { IsTransactional = true });

        var throwingTransaction = new TestTransactionApi(throwOnRollback: true);
        var succeedingTransaction = new TestTransactionApi();
        unitOfWork.AddTransactionApi("throwing", throwingTransaction);
        unitOfWork.AddTransactionApi("succeeding", succeedingTransaction);

        await unitOfWork.RollbackAsync();

        Assert.True(throwingTransaction.RollbackCalled);
        Assert.True(succeedingTransaction.RollbackCalled);
    }

    [Theory]
    [InlineData(UnitOfWorkTransactionBehavior.Auto, false, false)]
    [InlineData(UnitOfWorkTransactionBehavior.Auto, true, true)]
    [InlineData(UnitOfWorkTransactionBehavior.Enabled, false, true)]
    [InlineData(UnitOfWorkTransactionBehavior.Disabled, true, false)]
    public void UnitOfWorkDefaultOptions_CalculateIsTransactional_should_follow_behavior(
        UnitOfWorkTransactionBehavior behavior,
        bool autoValue,
        bool expected)
    {
        var options = new UnitOfWorkDefaultOptions { TransactionBehavior = behavior };

        var actual = options.CalculateIsTransactional(autoValue);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnitOfWorkDefaultOptions_CalculateIsTransactional_should_prefer_legacy_override()
    {
        var options = new UnitOfWorkDefaultOptions
        {
            TransactionBehavior = UnitOfWorkTransactionBehavior.Disabled,
            IsTransactional = true
        };

        Assert.True(options.CalculateIsTransactional(autoValue: false));
    }

    [Fact]
    public void ChildUnitOfWork_Dispose_should_unsubscribe_from_parent_events()
    {
        var parent = new StubUnitOfWork();
        var child = new ChildUnitOfWork(parent);
        var failedCount = 0;
        var disposedCount = 0;
        child.Failed += (_, _) => failedCount++;
        child.Disposed += (_, _) => disposedCount++;

        child.Dispose();
        parent.RaiseFailed();
        parent.RaiseDisposed();

        Assert.Equal(0, failedCount);
        Assert.Equal(0, disposedCount);
    }

    [Fact]
    public async Task IUnitOfWork_DefaultDisposeAsync_should_bridge_synchronous_implementations()
    {
        var unitOfWork = new StubUnitOfWork();

        await ((IAsyncDisposable)unitOfWork).DisposeAsync();

        Assert.Equal(1, unitOfWork.DisposeCount);
    }

    [Fact]
    public async Task UnitOfWorkManager_DisposeAsync_should_dispose_async_only_scoped_dependency()
    {
        await using var provider = CreateUnitOfWorkServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var unitOfWork = manager.Begin(new UnitOfWorkOptions(), requiresNew: true);
        var dependency = unitOfWork.ServiceProvider.GetRequiredService<AsyncOnlyScopedDependency>();

        await unitOfWork.DisposeAsync();

        Assert.True(dependency.IsDisposed);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void UnitOfWorkManager_Dispose_should_bridge_async_scope_cleanup_for_synchronous_callers()
    {
        using var provider = CreateUnitOfWorkServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var unitOfWork = manager.Begin(new UnitOfWorkOptions(), requiresNew: true);
        var dependency = unitOfWork.ServiceProvider.GetRequiredService<AsyncOnlyScopedDependency>();

        unitOfWork.Dispose();

        Assert.True(dependency.IsDisposed);
        Assert.Null(manager.Current);
    }

    [Fact]
    public async Task UnitOfWorkManager_DisposeAsync_should_preserve_child_and_requires_new_ambient_ownership()
    {
        await using var provider = CreateUnitOfWorkServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var ambient = provider.GetRequiredService<IAmbientUnitOfWork>();
        var outer = manager.Begin(new UnitOfWorkOptions(), requiresNew: true);

        var child = manager.Begin(new UnitOfWorkOptions());
        Assert.IsType<ChildUnitOfWork>(child);
        await child.DisposeAsync();
        Assert.Same(outer, ambient.UnitOfWork);

        var independent = manager.Begin(new UnitOfWorkOptions(), requiresNew: true);
        Assert.Same(independent, ambient.UnitOfWork);
        await independent.DisposeAsync();
        Assert.Same(outer, ambient.UnitOfWork);

        await outer.DisposeAsync();
        Assert.Null(ambient.UnitOfWork);
    }

    [Fact]
    public async Task UnitOfWorkManager_Begin_should_cleanup_scope_and_restore_ambient_when_initialization_fails()
    {
        await using var provider = CreateUnitOfWorkServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var ambient = provider.GetRequiredService<IAmbientUnitOfWork>();
        var cleanupTracker = provider.GetRequiredService<ScopeCleanupTracker>();
        var outer = manager.Begin(new UnitOfWorkOptions(), requiresNew: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            manager.Begin(new UnitOfWorkOptions { Timeout = 0 }, requiresNew: true));

        Assert.Contains("greater than zero", exception.Message, StringComparison.Ordinal);
        Assert.Same(outer, ambient.UnitOfWork);
        Assert.Equal(1, cleanupTracker.DisposeCount);

        await outer.DisposeAsync();
        Assert.Null(ambient.UnitOfWork);
    }

    [Fact]
    public async Task UnitOfWorkManager_RepeatedDisposeAsync_should_wait_for_active_scope_cleanup()
    {
        await using var provider = CreateUnitOfWorkServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var unitOfWork = manager.Begin(new UnitOfWorkOptions(), requiresNew: true);
        var dependency = unitOfWork.ServiceProvider.GetRequiredService<AsyncOnlyScopedDependency>();
        dependency.BlockDisposal();

        var firstDisposal = unitOfWork.DisposeAsync().AsTask();
        await dependency.DisposalStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var repeatedDisposal = unitOfWork.DisposeAsync().AsTask();
        Assert.False(repeatedDisposal.IsCompleted);

        dependency.AllowDisposal();
        await Task.WhenAll(firstDisposal, repeatedDisposal).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(dependency.IsDisposed);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void UnitOfWorkInterceptor_should_reject_synchronous_unit_of_work_method()
    {
        var manager = new StubUnitOfWorkManager();
        var interceptor = new UnitOfWorkInterceptor(
            manager,
            new UnitOfWorkSuppressionContext(),
            Options.Create(new UnitOfWorkDefaultOptions()));
        var proxy = new ProxyGenerator().CreateClassProxy<SyncUnitOfWorkService>(interceptor);

        var exception = Assert.Throws<InvalidOperationException>(() => proxy.Execute());

        Assert.Contains("Task", exception.Message, StringComparison.Ordinal);
        Assert.False(proxy.Executed);
    }

    [Fact]
    public void UnitOfWorkInterceptor_should_bypass_interception_when_external_transaction_owns_scope()
    {
        var manager = new StubUnitOfWorkManager();
        var suppression = new UnitOfWorkSuppressionContext();
        var interceptor = new UnitOfWorkInterceptor(
            manager,
            suppression,
            Options.Create(new UnitOfWorkDefaultOptions()));
        var proxy = new ProxyGenerator().CreateClassProxy<SyncUnitOfWorkService>(interceptor);

        using (suppression.Suppress())
        {
            proxy.Execute();
        }

        Assert.True(proxy.Executed);
        Assert.Null(manager.Current);
    }

    [Fact]
    public async Task UnitOfWorkInterceptor_should_honor_disabled_implementation_metadata_over_interface_marker()
    {
        var manager = new StubUnitOfWorkManager();
        var interceptor = new UnitOfWorkInterceptor(
            manager,
            new UnitOfWorkSuppressionContext(),
            Options.Create(new UnitOfWorkDefaultOptions()));
        var target = new DisabledImplementationService();
        var proxy = new ProxyGenerator().CreateInterfaceProxyWithTarget<IDisabledImplementationService>(
            target,
            interceptor);

        await proxy.ExecuteAsync();

        Assert.True(target.Executed);
    }

    private static ServiceProvider CreateUnitOfWorkServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<UnitOfWorkDefaultOptions>(_ => { });
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.AddSingleton<ScopeCleanupTracker>();
        services.AddTransient<IUnitOfWork>(serviceProvider =>
        {
            _ = serviceProvider.GetRequiredService<ScopeCleanupSentinel>();
            return new UnitOfWork(
                serviceProvider.GetRequiredService<IOptions<UnitOfWorkDefaultOptions>>(),
                serviceProvider);
        });
        services.AddScoped<ScopeCleanupSentinel>();
        services.AddScoped<AsyncOnlyScopedDependency>();
        return services.BuildServiceProvider();
    }

    private sealed class AsyncOnlyScopedDependency : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continueDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _blockDisposal;

        public bool IsDisposed { get; private set; }
        public Task DisposalStarted => _disposalStarted.Task;

        public void BlockDisposal()
        {
            _blockDisposal = true;
        }

        public void AllowDisposal()
        {
            _continueDisposal.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            _disposalStarted.TrySetResult();
            if (_blockDisposal)
            {
                await _continueDisposal.Task;
            }

            IsDisposed = true;
        }
    }

    private sealed class ScopeCleanupTracker
    {
        public int DisposeCount { get; private set; }

        public void RecordDisposal()
        {
            DisposeCount++;
        }
    }

    private sealed class ScopeCleanupSentinel(ScopeCleanupTracker cleanupTracker) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            cleanupTracker.RecordDisposal();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTransactionApi : ITransactionApi
    {
        private readonly bool _throwOnRollback;

        public bool RollbackCalled { get; private set; }

        public TestTransactionApi(bool throwOnRollback = false)
        {
            _throwOnRollback = throwOnRollback;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCalled = true;
            if (_throwOnRollback)
            {
                throw new InvalidOperationException("rollback failure");
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubUnitOfWork : IUnitOfWork
    {
        public int DisposeCount { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public event EventHandler<UnitOfWorkFailedEventArgs>? Failed;
        public event EventHandler<UnitOfWorkEventArgs>? Disposed;
        public UnitOfWorkOptions Options { get; } = new();
        public IUnitOfWork? Outer { get; set; }
        public bool IsReserved { get; set; }
        public bool IsDisposed { get; set; }
        public bool IsCompleted { get; set; }
        public string? ReservationName { get; set; }
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public void SetOuter(IUnitOfWork? outer) => Outer = outer;
        public void Initialize(UnitOfWorkOptions options) { }
        public void Reserve(string reservationName) => ReservationName = reservationName;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void OnCompleted(Func<Task> handler) { }
        public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => factory(key);
        public IDatabaseApi? FindDatabaseApi(string key) => null;
        public void AddDatabaseApi(string key, IDatabaseApi api) { }
        public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();
        public ITransactionApi? FindTransactionApi(string key) => null;
        public void AddTransactionApi(string key, ITransactionApi api) { }
        public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();
        public void Dispose() => DisposeCount++;
        public void RaiseFailed() => Failed?.Invoke(this, new UnitOfWorkFailedEventArgs(this, null, false));
        public void RaiseDisposed() => Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
    }

    private sealed class StubUnitOfWorkManager : IUnitOfWorkManager
    {
        public IUnitOfWork? Current => null;
        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => throw new InvalidOperationException("must not begin");
        public IUnitOfWork Begin(bool requiresNew = false) => throw new InvalidOperationException("must not begin");
        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }

    public interface IDisabledImplementationService : IUnitOfWorkEnabled
    {
        Task ExecuteAsync();
    }

    [UnitOfWork(IsDisabled = true)]
    public sealed class DisabledImplementationService : IDisabledImplementationService
    {
        public bool Executed { get; private set; }

        public Task ExecuteAsync()
        {
            Executed = true;
            return Task.CompletedTask;
        }
    }

    public class SyncUnitOfWorkService
    {
        public bool Executed { get; private set; }

        [UnitOfWork]
        public virtual void Execute()
        {
            Executed = true;
        }
    }
}
