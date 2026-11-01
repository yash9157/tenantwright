using System.Data;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Modularity.Context;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Attributes;
using Shared.Uow.Events;
using Shared.Uow.Implementations;
using Shared.Uow.Options;
using Xunit;

namespace Shared.Modularity.Tests;

public class UnitOfWorkRequestFilterTests
{
    private const int ExpectedFilterOrder = int.MinValue + 100;

    [Fact]
    public void UowModule_should_register_action_and_page_filters_with_early_order()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters
            .OfType<ServiceFilterAttribute>()
            .ToArray();

        var actionFilter = Assert.Single(filters, filter =>
            typeof(IAsyncActionFilter).IsAssignableFrom(filter.ServiceType));
        var pageFilter = Assert.Single(filters, filter =>
            typeof(IAsyncPageFilter).IsAssignableFrom(filter.ServiceType));

        Assert.Equal(ExpectedFilterOrder, actionFilter.Order);
        Assert.Equal(ExpectedFilterOrder, pageFilter.Order);
        Assert.IsAssignableFrom<IAsyncActionFilter>(provider.GetRequiredService(actionFilter.ServiceType));
        Assert.IsAssignableFrom<IAsyncPageFilter>(provider.GetRequiredService(pageFilter.ServiceType));
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("OPTIONS", false)]
    [InlineData("TRACE", false)]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    [InlineData("CUSTOM", true)]
    public async Task Action_filter_should_apply_fail_safe_http_transaction_defaults(
        string httpMethod,
        bool expectedTransactional)
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, httpMethod);

        await InvokeActionFilterAsync(
            provider,
            httpContext,
            GetMethod<DefaultController>(nameof(DefaultController.ExecuteAsync)));

        Assert.Equal(expectedTransactional, manager.LastRequestedOptions?.IsTransactional);
        Assert.Equal(1, manager.ReservedUnitOfWork.SaveCount);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
    }

    [Fact]
    public async Task Action_filter_should_prefer_method_metadata_over_class_metadata()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);

        await InvokeActionFilterAsync(
            provider,
            httpContext,
            GetMethod<TransactionalController>(nameof(TransactionalController.NonTransactionalAsync)));

        Assert.False(Assert.IsType<bool>(manager.LastRequestedOptions?.IsTransactional));
    }

    [Fact]
    public async Task Action_filter_should_use_class_metadata_before_http_defaults()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Get);

        await InvokeActionFilterAsync(
            provider,
            httpContext,
            GetMethod<TransactionalController>(nameof(TransactionalController.ExecuteAsync)));

        Assert.True(Assert.IsType<bool>(manager.LastRequestedOptions?.IsTransactional));
    }

    [Fact]
    public async Task Action_filter_should_reject_disabled_metadata_with_scope_options()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Get);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeActionFilterAsync(
                provider,
                httpContext,
                GetMethod<InvalidMetadataController>(nameof(InvalidMetadataController.DisabledRequiresNewAsync))));

        Assert.Contains("contradictory", exception.Message, StringComparison.Ordinal);
        Assert.False(manager.TryBeginReservedCalled);
    }

    [Fact]
    public async Task Action_filter_should_reject_non_transactional_isolation_level()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Get);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeActionFilterAsync(
                provider,
                httpContext,
                GetMethod<InvalidMetadataController>(nameof(InvalidMetadataController.NonTransactionalIsolationAsync))));

        Assert.Contains("isolation level", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.TryBeginReservedCalled);
    }

    [Fact]
    public async Task Action_filter_should_reject_isolation_level_when_http_default_is_non_transactional()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Get);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeActionFilterAsync(
                provider,
                httpContext,
                GetMethod<InvalidMetadataController>(nameof(InvalidMetadataController.IsolationWithHttpDefaultAsync))));

        Assert.Contains("effectively transactional", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.TryBeginReservedCalled);
    }

    [Fact]
    public async Task Disabled_action_should_execute_without_activating_a_unit_of_work()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);
        var actionCalled = false;

        await InvokeActionFilterAsync(
            provider,
            httpContext,
            GetMethod<DisabledController>(nameof(DisabledController.ExecuteAsync)),
            () =>
            {
                actionCalled = true;
                return null;
            });

        Assert.True(actionCalled);
        Assert.False(manager.TryBeginReservedCalled);
        Assert.Empty(manager.OwnedUnitOfWorks);
    }

    [Fact]
    public async Task Disabled_action_failure_should_prevent_nested_reserved_scope_completion()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);
        var actionFailure = new InvalidOperationException("disabled action failed");
        var middleware = new UnitOfWorkMiddleware(
            async context =>
            {
                await InvokeActionFilterAsync(
                    provider,
                    context,
                    GetMethod<DisabledController>(nameof(DisabledController.ExecuteAsync)),
                    () =>
                    {
                        Assert.True(manager.TryBeginReserved(
                            UnitOfWork.UnitOfWorkReservationName,
                            new UnitOfWorkOptions { IsTransactional = true }));
                        return actionFailure;
                    });

                context.Response.StatusCode = StatusCodes.Status409Conflict;
            },
            Options.Create(new UnitOfWorkMiddlewareOptions()),
            NullLogger<UnitOfWorkMiddleware>.Instance);

        await middleware.InvokeAsync(httpContext, manager);

        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        Assert.Equal(1, manager.ReservedUnitOfWork.RollbackCount);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
        Assert.Equal(1, manager.ReservedUnitOfWork.DisposeCount);
    }

    [Fact]
    public async Task Action_filter_should_save_reserved_scope_before_returning_to_result_execution()
    {
        var events = new List<string>();
        var manager = new RecordingUnitOfWorkManager(events);
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);

        await InvokeActionFilterAsync(
            provider,
            httpContext,
            GetMethod<DefaultController>(nameof(DefaultController.ExecuteAsync)),
            () =>
            {
                events.Add("action");
                return null;
            });
        events.Add("result");

        Assert.Equal(["action", "save", "result"], events);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
        Assert.Equal(0, manager.ReservedUnitOfWork.DisposeCount);
    }

    [Fact]
    public async Task Handled_late_action_failure_should_rollback_and_prevent_middleware_completion()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);
        var actionFailure = new InvalidOperationException("action failed");
        var actionFilter = ResolveActionFilter(provider);
        var methodInfo = GetMethod<DefaultController>(nameof(DefaultController.ExecuteAsync));
        var middleware = new UnitOfWorkMiddleware(
            async context =>
            {
                await InvokeActionFilterAsync(
                    actionFilter,
                    context,
                    methodInfo,
                    () => actionFailure);

                // Simulates an outer MVC exception filter converting the action failure to a response.
                context.Response.StatusCode = StatusCodes.Status409Conflict;
            },
            Options.Create(new UnitOfWorkMiddlewareOptions()),
            NullLogger<UnitOfWorkMiddleware>.Instance);

        await middleware.InvokeAsync(httpContext, manager);

        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        Assert.Equal(1, manager.ReservedUnitOfWork.RollbackCount);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
        Assert.Equal(1, manager.ReservedUnitOfWork.DisposeCount);
    }

    [Fact]
    public async Task Handled_action_failure_should_preserve_response_when_request_scope_disposal_fails()
    {
        var disposalFailure = new InvalidOperationException("dispose failed");
        var manager = new RecordingUnitOfWorkManager
        {
            ReservedDisposeException = disposalFailure
        };
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);
        var actionFailure = new InvalidOperationException("action failed");
        var middleware = new UnitOfWorkMiddleware(
            async context =>
            {
                await InvokeActionFilterAsync(
                    provider,
                    context,
                    GetMethod<DefaultController>(nameof(DefaultController.ExecuteAsync)),
                    () => actionFailure);

                context.Response.StatusCode = StatusCodes.Status409Conflict;
            },
            Options.Create(new UnitOfWorkMiddlewareOptions()),
            NullLogger<UnitOfWorkMiddleware>.Instance);

        await middleware.InvokeAsync(httpContext, manager);

        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        Assert.Equal(1, manager.ReservedUnitOfWork.RollbackCount);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
        Assert.Equal(1, manager.ReservedUnitOfWork.DisposeCount);
    }

    [Fact]
    public async Task Middleware_should_rollback_with_cleanup_token_and_preserve_pipeline_exception()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);
        httpContext.RequestAborted = requestCancellation.Token;
        var expected = new InvalidOperationException("pipeline failed");
        var middleware = new UnitOfWorkMiddleware(
            _ => throw expected,
            Options.Create(new UnitOfWorkMiddlewareOptions()),
            NullLogger<UnitOfWorkMiddleware>.Instance);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(httpContext, manager));

        Assert.Same(expected, actual);
        Assert.Equal(1, manager.ReservedUnitOfWork.RollbackCount);
        Assert.False(manager.ReservedUnitOfWork.RollbackCancellationToken.IsCancellationRequested);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
    }

    [Fact]
    public async Task Action_filter_should_own_complete_and_dispose_fallback_scope_without_middleware()
    {
        var manager = new RecordingUnitOfWorkManager
        {
            ReservedScopeAvailable = false
        };
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);

        await InvokeActionFilterAsync(
            provider,
            httpContext,
            GetMethod<DefaultController>(nameof(DefaultController.ExecuteAsync)));

        var unitOfWork = Assert.Single(manager.OwnedUnitOfWorks);
        Assert.True(unitOfWork.Options.IsTransactional);
        Assert.Equal(2, unitOfWork.SaveCount);
        Assert.Equal(1, unitOfWork.CompleteCount);
        Assert.Equal(1, unitOfWork.DisposeCount);
    }

    [Fact]
    public async Task RequiresNew_action_should_bypass_reservation_and_own_scope()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);

        await InvokeActionFilterAsync(
            provider,
            httpContext,
            GetMethod<RequiresNewController>(nameof(RequiresNewController.ExecuteAsync)));

        Assert.False(manager.TryBeginReservedCalled);
        Assert.True(manager.LastBeginRequiresNew);
        var unitOfWork = Assert.Single(manager.OwnedUnitOfWorks);
        Assert.Equal(1, unitOfWork.CompleteCount);
        Assert.Equal(1, unitOfWork.DisposeCount);
    }

    [Fact]
    public async Task Ignored_mvc_path_should_not_activate_filter_fallback_scope()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);
        httpContext.Request.Path = "/ignored/action";
        var options = new UnitOfWorkMiddlewareOptions();
        options.IgnoredUrls.Add("/ignored");
        var middleware = new UnitOfWorkMiddleware(
            context => InvokeActionFilterAsync(
                provider,
                context,
                GetMethod<DefaultController>(nameof(DefaultController.ExecuteAsync))),
            Options.Create(options),
            NullLogger<UnitOfWorkMiddleware>.Instance);

        await middleware.InvokeAsync(httpContext, manager);

        Assert.False(manager.TryBeginReservedCalled);
        Assert.Empty(manager.OwnedUnitOfWorks);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
    }

    [Fact]
    public async Task Ignored_razor_path_should_not_activate_filter_fallback_scope()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);
        httpContext.Request.Path = "/ignored/page";
        var options = new UnitOfWorkMiddlewareOptions();
        options.IgnoredUrls.Add("/ignored");
        var middleware = new UnitOfWorkMiddleware(
            context => InvokePageFilterAsync(
                provider,
                context,
                GetMethod<DefaultPageModel>(nameof(DefaultPageModel.OnGetAsync))),
            Options.Create(options),
            NullLogger<UnitOfWorkMiddleware>.Instance);

        await middleware.InvokeAsync(httpContext, manager);

        Assert.False(manager.TryBeginReservedCalled);
        Assert.Empty(manager.OwnedUnitOfWorks);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
    }

    [Fact]
    public async Task Cancelled_pre_result_save_should_rollback_with_non_cancelled_cleanup_token()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();
        var httpContext = CreateHttpContext(provider, HttpMethods.Post);
        httpContext.RequestAborted = requestCancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeActionFilterAsync(
                provider,
                httpContext,
                GetMethod<DefaultController>(nameof(DefaultController.ExecuteAsync))));

        Assert.Equal(1, manager.ReservedUnitOfWork.RollbackCount);
        Assert.False(manager.ReservedUnitOfWork.RollbackCancellationToken.IsCancellationRequested);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
    }

    [Fact]
    public async Task Razor_page_filter_should_apply_the_same_request_policy_and_save_behavior()
    {
        var manager = new RecordingUnitOfWorkManager();
        using var provider = CreateServiceProvider(manager);
        var httpContext = CreateHttpContext(provider, HttpMethods.Get);

        await InvokePageFilterAsync(
            provider,
            httpContext,
            GetMethod<DefaultPageModel>(nameof(DefaultPageModel.OnGetAsync)));

        Assert.False(Assert.IsType<bool>(manager.LastRequestedOptions?.IsTransactional));
        Assert.Equal(1, manager.ReservedUnitOfWork.SaveCount);
        Assert.Equal(0, manager.ReservedUnitOfWork.CompleteCount);
    }

    private static ServiceProvider CreateServiceProvider(RecordingUnitOfWorkManager manager)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddControllers();
        services.AddSingleton<IUnitOfWorkManager>(manager);

        var configuration = new ConfigurationBuilder().Build();
        new UowModule().ConfigureServices(new ServiceConfigurationContext(services, configuration));
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static DefaultHttpContext CreateHttpContext(
        IServiceProvider serviceProvider,
        string httpMethod)
    {
        return new DefaultHttpContext
        {
            RequestServices = serviceProvider,
            Request =
            {
                Method = httpMethod,
                Path = "/test"
            }
        };
    }

    private static async Task InvokeActionFilterAsync(
        IServiceProvider serviceProvider,
        HttpContext httpContext,
        MethodInfo methodInfo,
        Func<Exception?>? action = null)
    {
        await InvokeActionFilterAsync(
            ResolveActionFilter(serviceProvider),
            httpContext,
            methodInfo,
            action ?? (() => null));
    }

    private static async Task InvokeActionFilterAsync(
        IAsyncActionFilter filter,
        HttpContext httpContext,
        MethodInfo methodInfo,
        Func<Exception?> action)
    {
        var descriptor = new ControllerActionDescriptor
        {
            ActionName = methodInfo.Name,
            ControllerName = methodInfo.DeclaringType!.Name,
            ControllerTypeInfo = methodInfo.DeclaringType.GetTypeInfo(),
            MethodInfo = methodInfo
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var filters = new List<IFilterMetadata>();
        var controller = new object();
        var executingContext = new ActionExecutingContext(
            actionContext,
            filters,
            new Dictionary<string, object?>(),
            controller);

        await filter.OnActionExecutionAsync(
            executingContext,
            () => Task.FromResult(new ActionExecutedContext(actionContext, filters, controller)
            {
                Exception = action()
            }));
    }

    private static async Task InvokePageFilterAsync(
        IServiceProvider serviceProvider,
        HttpContext httpContext,
        MethodInfo methodInfo)
    {
        var filter = ResolvePageFilter(serviceProvider);
        var actionDescriptor = new CompiledPageActionDescriptor();
        var actionContext = new ActionContext(httpContext, new RouteData(), actionDescriptor);
        var pageContext = new PageContext(actionContext);
        var filters = new List<IFilterMetadata>();
        var handler = new HandlerMethodDescriptor
        {
            MethodInfo = methodInfo
        };
        var pageModel = new object();
        var executingContext = new PageHandlerExecutingContext(
            pageContext,
            filters,
            handler,
            new Dictionary<string, object?>(),
            pageModel);

        await filter.OnPageHandlerExecutionAsync(
            executingContext,
            () => Task.FromResult(new PageHandlerExecutedContext(
                pageContext,
                filters,
                handler,
                pageModel)));
    }

    private static IAsyncActionFilter ResolveActionFilter(IServiceProvider serviceProvider)
    {
        var serviceFilter = serviceProvider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters
            .OfType<ServiceFilterAttribute>()
            .Single(filter => typeof(IAsyncActionFilter).IsAssignableFrom(filter.ServiceType));
        return Assert.IsAssignableFrom<IAsyncActionFilter>(
            serviceProvider.GetRequiredService(serviceFilter.ServiceType));
    }

    private static IAsyncPageFilter ResolvePageFilter(IServiceProvider serviceProvider)
    {
        var serviceFilter = serviceProvider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters
            .OfType<ServiceFilterAttribute>()
            .Single(filter => typeof(IAsyncPageFilter).IsAssignableFrom(filter.ServiceType));
        return Assert.IsAssignableFrom<IAsyncPageFilter>(
            serviceProvider.GetRequiredService(serviceFilter.ServiceType));
    }

    private static MethodInfo GetMethod<T>(string name)
    {
        return typeof(T).GetMethod(name) ??
               throw new InvalidOperationException($"Could not find method {typeof(T).FullName}.{name}.");
    }

    private sealed class RecordingUnitOfWorkManager
        : IUnitOfWorkManager
    {
        private readonly List<string>? _events;
        private IUnitOfWork? _current;

        public RecordingUnitOfWorkManager(List<string>? events = null)
        {
            _events = events;
            ReservedUnitOfWork = new RecordingUnitOfWork(new UnitOfWorkOptions(), events);
        }

        public bool ReservedScopeAvailable { get; init; } = true;
        public Exception? ReservedDisposeException { get; init; }
        public bool TryBeginReservedCalled { get; private set; }
        public bool LastBeginRequiresNew { get; private set; }
        public UnitOfWorkOptions? LastRequestedOptions { get; private set; }
        public RecordingUnitOfWork ReservedUnitOfWork { get; private set; }
        public List<RecordingUnitOfWork> OwnedUnitOfWorks { get; } = [];
        public IUnitOfWork? Current => _current;

        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false)
        {
            LastBeginRequiresNew = requiresNew;
            LastRequestedOptions = options.Clone();
            RecordingUnitOfWork? unitOfWork = null;
            unitOfWork = new RecordingUnitOfWork(
                options.Clone(),
                _events,
                () =>
                {
                    if (ReferenceEquals(_current, unitOfWork))
                    {
                        _current = null;
                    }
                });
            OwnedUnitOfWorks.Add(unitOfWork);
            _current = unitOfWork;
            return unitOfWork;
        }

        public IUnitOfWork Begin(bool requiresNew = false)
        {
            return Begin(new UnitOfWorkOptions(), requiresNew);
        }

        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false)
        {
            ReservedUnitOfWork = new RecordingUnitOfWork(
                new UnitOfWorkOptions(),
                _events,
                disposeException: ReservedDisposeException)
            {
                IsReserved = true,
                ReservationName = reservationName
            };
            _current = null;
            return ReservedUnitOfWork;
        }

        public void BeginReserved(string reservationName, UnitOfWorkOptions options)
        {
            if (!TryBeginReserved(reservationName, options))
            {
                throw new InvalidOperationException("Reserved unit of work was unavailable.");
            }
        }

        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options)
        {
            TryBeginReservedCalled = true;
            LastRequestedOptions = options.Clone();
            if (!ReservedScopeAvailable)
            {
                return false;
            }

            ReservedUnitOfWork.Initialize(options);
            _current = ReservedUnitOfWork;
            return true;
        }
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        private readonly List<string>? _events;
        private readonly Action? _onDispose;
        private readonly Exception? _disposeException;
        private bool _isRolledBack;

        public RecordingUnitOfWork(
            UnitOfWorkOptions options,
            List<string>? events = null,
            Action? onDispose = null,
            Exception? disposeException = null)
        {
            Options = options;
            _events = events;
            _onDispose = onDispose;
            _disposeException = disposeException;
        }

        public int SaveCount { get; private set; }
        public int CompleteCount { get; private set; }
        public int RollbackCount { get; private set; }
        public int DisposeCount { get; private set; }
        public CancellationToken RollbackCancellationToken { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public event EventHandler<UnitOfWorkFailedEventArgs>? Failed
        {
            add { }
            remove { }
        }
        public event EventHandler<UnitOfWorkEventArgs>? Disposed
        {
            add { }
            remove { }
        }
        public UnitOfWorkOptions Options { get; private set; }
        public IUnitOfWork? Outer => null;
        public bool IsReserved { get; set; }
        public bool IsDisposed => DisposeCount != 0;
        public bool IsCompleted => CompleteCount != 0;
        public string? ReservationName { get; set; }
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public void SetOuter(IUnitOfWork? outer) { }

        public void Initialize(UnitOfWorkOptions options)
        {
            Options = options.Clone();
            IsReserved = false;
        }

        public void Reserve(string reservationName)
        {
            IsReserved = true;
            ReservationName = reservationName;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            _events?.Add("save");
            return Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteCount++;
            await SaveChangesAsync(cancellationToken);
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (_isRolledBack)
            {
                return Task.CompletedTask;
            }

            _isRolledBack = true;
            RollbackCount++;
            RollbackCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }

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
            DisposeCount++;
            _onDispose?.Invoke();
            if (_disposeException is not null)
            {
                throw _disposeException;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DefaultController
    {
        private int _invocationCount;

        public Task ExecuteAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }
    }

    [UnitOfWork(isTransactional: true)]
    private sealed class TransactionalController
    {
        private int _invocationCount;

        public Task ExecuteAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }

        [UnitOfWork(isTransactional: false)]
        public Task NonTransactionalAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class DisabledController
    {
        private int _invocationCount;

        [UnitOfWork(IsDisabled = true)]
        public Task ExecuteAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RequiresNewController
    {
        private int _invocationCount;

        [UnitOfWork(RequiresNew = true)]
        public Task ExecuteAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class IsolationOnlyUnitOfWorkAttribute : UnitOfWorkAttribute
    {
        public IsolationOnlyUnitOfWorkAttribute()
        {
            IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
        }
    }

    private sealed class InvalidMetadataController
    {
        private int _invocationCount;

        [UnitOfWork(IsDisabled = true, RequiresNew = true)]
        public Task DisabledRequiresNewAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }

        [UnitOfWork(false, IsolationLevel.ReadCommitted)]
        public Task NonTransactionalIsolationAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }

        [IsolationOnlyUnitOfWork]
        public Task IsolationWithHttpDefaultAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class DefaultPageModel : PageModel
    {
        private int _invocationCount;

        public Task OnGetAsync()
        {
            _invocationCount++;
            return Task.CompletedTask;
        }
    }
}
