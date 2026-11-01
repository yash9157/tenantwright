using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shared.AuditLogs.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Middleware;
using Shared.CurrentUser;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Xunit;

namespace Shared.AuditLogs.Tests;

public class AuditingBehaviorTests
{
    [Fact]
    public void ShouldSaveAudit_should_follow_attribute_and_integration_precedence()
    {
        var helper = CreateAuditingHelper(isEnabled: true);

        var auditedMethod = typeof(SampleAuditedService).GetMethod(nameof(SampleAuditedService.AuditedMethod))!;
        var disabledMethod = typeof(SampleAuditedService).GetMethod(nameof(SampleAuditedService.DisabledMethod))!;
        var integrationMethod = typeof(SampleIntegrationService).GetMethod(nameof(SampleIntegrationService.ExecuteAsync))!;

        Assert.True(helper.ShouldSaveAudit(auditedMethod, defaultValue: false));
        Assert.False(helper.ShouldSaveAudit(disabledMethod, defaultValue: true));
        Assert.False(helper.ShouldSaveAudit(integrationMethod, defaultValue: true, ignoreIntegrationServiceAttribute: false));
        Assert.False(helper.ShouldSaveAudit(integrationMethod, defaultValue: true, ignoreIntegrationServiceAttribute: true));
    }

    [Fact]
    public async Task Middleware_should_save_uow_before_audit_store()
    {
        var order = new List<string>();
        var uowMock = new Mock<IUnitOfWork>(MockBehavior.Strict);
        uowMock.SetupGet(x => x.IsDisposed).Returns(false);
        uowMock.SetupGet(x => x.IsCompleted).Returns(false);
        uowMock.SetupGet(x => x.IsReserved).Returns(false);
        uowMock.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(_ =>
            {
                order.Add("uow-save");
                return Task.CompletedTask;
            });

        var uowManagerMock = new Mock<IUnitOfWorkManager>(MockBehavior.Strict);
        uowManagerMock.SetupGet(x => x.Current).Returns(uowMock.Object);

        var auditingManager = new TestAuditingManager(order);
        var middleware = new AuditLogCaptureMiddleware(
            new TestOptionsMonitor<SharedAuditingOptions>(new SharedAuditingOptions
            {
                IsEnabled = true,
                IsEnabledForGetRequests = false
            }),
            Options.Create(new SharedAspNetCoreAuditingOptions()),
            Options.Create(new AuditRedactionOptions()),
            auditingManager,
            uowManagerMock.Object,
            NullLogger<AuditLogCaptureMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/test";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(2, order.Count);
        Assert.Equal("uow-save", order[0]);
        Assert.Equal("audit-save", order[1]);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Middleware_should_not_save_for_get_or_head_when_disabled(string method)
    {
        var auditingManager = new TrackingAuditingManager();
        var middleware = new AuditLogCaptureMiddleware(
            new TestOptionsMonitor<SharedAuditingOptions>(new SharedAuditingOptions
            {
                IsEnabled = true,
                IsEnabledForGetRequests = false
            }),
            Options.Create(new SharedAspNetCoreAuditingOptions()),
            Options.Create(new AuditRedactionOptions()),
            auditingManager,
            new Mock<IUnitOfWorkManager>(MockBehavior.Strict).Object,
            NullLogger<AuditLogCaptureMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = "/api/sample";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(0, auditingManager.SaveCount);
    }

    [Fact]
    public async Task Middleware_should_not_save_for_anonymous_user_when_disabled()
    {
        var auditingManager = new TrackingAuditingManager();
        var middleware = new AuditLogCaptureMiddleware(
            new TestOptionsMonitor<SharedAuditingOptions>(new SharedAuditingOptions
            {
                IsEnabled = true,
                IsEnabledForGetRequests = true,
                IsEnabledForAnonymousUsers = false
            }),
            Options.Create(new SharedAspNetCoreAuditingOptions()),
            Options.Create(new AuditRedactionOptions()),
            auditingManager,
            new Mock<IUnitOfWorkManager>(MockBehavior.Strict).Object,
            NullLogger<AuditLogCaptureMiddleware>.Instance);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/sample";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(0, auditingManager.SaveCount);
    }

    [Fact]
    public async Task Middleware_should_not_save_when_request_audit_requires_policy_and_missing_decision()
    {
        var uowManagerMock = new Mock<IUnitOfWorkManager>(MockBehavior.Strict);
        uowManagerMock.SetupGet(x => x.Current).Returns((IUnitOfWork?)null);

        var auditingManager = new TrackingAuditingManager();
        var middleware = new AuditLogCaptureMiddleware(
            new TestOptionsMonitor<SharedAuditingOptions>(new SharedAuditingOptions
            {
                IsEnabled = true,
                IsEnabledForGetRequests = true,
                WriteRequestAuditOnlyForAuditedEndpoints = true
            }),
            Options.Create(new SharedAspNetCoreAuditingOptions()),
            Options.Create(new AuditRedactionOptions()),
            auditingManager,
            uowManagerMock.Object,
            NullLogger<AuditLogCaptureMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/sample";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(0, auditingManager.SaveCount);
    }

    [Fact]
    public async Task Middleware_should_save_when_request_audit_requires_policy_and_audited_decision()
    {
        var uowManagerMock = new Mock<IUnitOfWorkManager>(MockBehavior.Strict);
        uowManagerMock.SetupGet(x => x.Current).Returns((IUnitOfWork?)null);

        var auditingManager = new TrackingAuditingManager();
        var middleware = new AuditLogCaptureMiddleware(
            new TestOptionsMonitor<SharedAuditingOptions>(new SharedAuditingOptions
            {
                IsEnabled = true,
                IsEnabledForGetRequests = true,
                WriteRequestAuditOnlyForAuditedEndpoints = true
            }),
            Options.Create(new SharedAspNetCoreAuditingOptions()),
            Options.Create(new AuditRedactionOptions()),
            auditingManager,
            uowManagerMock.Object,
            NullLogger<AuditLogCaptureMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Items[AuditingHttpContextItems.PolicyResultKey] = AuditPolicyResult.Audited();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/sample";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(1, auditingManager.SaveCount);
    }

    [Fact]
    public void AuditPolicyEvaluator_should_skip_controller_without_explicit_marker_in_strict_mode()
    {
        var evaluator = new AuditPolicyEvaluator();
        var method = typeof(UnannotatedController).GetMethod(nameof(UnannotatedController.PostAsync))!;
        var options = new SharedAuditingOptions
        {
            RequireExplicitControllerAuditing = true,
            EnforcementMode = AuditEnforcementMode.Strict
        };

        var result = evaluator.EvaluateControllerPolicy(
            typeof(UnannotatedController),
            method,
            shouldAuditByRuntimeRules: true,
            options);

        Assert.False(result.ShouldAudit);
        Assert.Equal(AuditDecision.SkippedByPolicy, result.Decision);
        Assert.Equal("policy_explicit_required", result.Reason);
    }

    [Fact]
    public void AuditingHelper_should_serialize_arguments_with_disable_auditing_attribute()
    {
        var helper = CreateAuditingHelper(isEnabled: true);
        var method = typeof(SampleAuditedService).GetMethod(nameof(SampleAuditedService.WithPayload))!;

        var action = helper.CreateAuditLogAction(
            typeof(SampleAuditedService),
            method,
            [new SamplePayload { Public = "visible", Secret = "hidden" }]);

        using var jsonDocument = JsonDocument.Parse(action.Parameters);
        var root = jsonDocument.RootElement;
        Assert.True(root.TryGetProperty("payload", out var payloadElement));
        Assert.True(payloadElement.TryGetProperty("public", out var publicElement));
        Assert.Equal("visible", publicElement.GetString());
        Assert.False(payloadElement.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task Middleware_should_rethrow_when_a_strict_category_audit_write_fails_even_with_hide_errors()
    {
        // Sub-flow 3 fail-closed: a security-critical category must propagate the audit-write failure so
        // the request fails, even though HideErrors keeps the rest of the surface in Observe mode.
        var auditingManager = new ConfigurableAuditingManager("auth", failOnSave: true);
        var middleware = BuildMiddlewareForStrict(auditingManager, new SharedAuditingOptions
        {
            IsEnabled = true,
            IsEnabledForGetRequests = true,
            HideErrors = true,
            StrictFailureCategories = { "auth" }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(BuildPostContext(), _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Middleware_should_swallow_a_non_strict_category_audit_write_failure_when_hide_errors()
    {
        var auditingManager = new ConfigurableAuditingManager("http.request", failOnSave: true);
        var middleware = BuildMiddlewareForStrict(auditingManager, new SharedAuditingOptions
        {
            IsEnabled = true,
            IsEnabledForGetRequests = true,
            HideErrors = true,
            StrictFailureCategories = { "auth" }
        });

        // Observe mode: the failure is logged and swallowed, the request still succeeds.
        await middleware.InvokeAsync(BuildPostContext(), _ => Task.CompletedTask);
    }

    [Fact]
    public async Task Middleware_should_preserve_an_explicitly_set_audit_category()
    {
        var auditingManager = new ConfigurableAuditingManager("permissions.grant", failOnSave: false);
        var middleware = BuildMiddlewareForStrict(auditingManager, new SharedAuditingOptions
        {
            IsEnabled = true,
            IsEnabledForGetRequests = true
        });

        await middleware.InvokeAsync(BuildPostContext(), _ => Task.CompletedTask);

        Assert.Equal("permissions.grant", auditingManager.Log.Category);
    }

    [Fact]
    public async Task Middleware_should_default_an_unset_audit_category_to_http_request()
    {
        var auditingManager = new ConfigurableAuditingManager(category: null, failOnSave: false);
        var middleware = BuildMiddlewareForStrict(auditingManager, new SharedAuditingOptions
        {
            IsEnabled = true,
            IsEnabledForGetRequests = true
        });

        await middleware.InvokeAsync(BuildPostContext(), _ => Task.CompletedTask);

        Assert.Equal("http.request", auditingManager.Log.Category);
    }

    private static AuditLogCaptureMiddleware BuildMiddlewareForStrict(
        IAuditingManager auditingManager,
        SharedAuditingOptions options)
    {
        var uowManagerMock = new Mock<IUnitOfWorkManager>(MockBehavior.Strict);
        uowManagerMock.SetupGet(x => x.Current).Returns((IUnitOfWork?)null);

        return new AuditLogCaptureMiddleware(
            new TestOptionsMonitor<SharedAuditingOptions>(options),
            Options.Create(new SharedAspNetCoreAuditingOptions()),
            Options.Create(new AuditRedactionOptions()),
            auditingManager,
            uowManagerMock.Object,
            NullLogger<AuditLogCaptureMiddleware>.Instance);
    }

    private static DefaultHttpContext BuildPostContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/sample";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        return context;
    }

    private static AuditingHelper CreateAuditingHelper(bool isEnabled)
    {
        return new AuditingHelper(
            Options.Create(new SharedAuditingOptions
            {
                IsEnabled = isEnabled
            }),
            new TestCurrentUser(),
            new TestCurrentTenant(),
            new JsonAuditSerializer(Options.Create(new SharedAuditingOptions
            {
                IsEnabled = isEnabled
            })),
            NullLogger<AuditingHelper>.Instance,
            new ServiceCollection().BuildServiceProvider());
    }

    private sealed class SampleAuditedService
    {
        [Audited]
        public void AuditedMethod()
        {
        }

        [DisableAuditing]
        public void DisabledMethod()
        {
        }

        public void WithPayload(SamplePayload payload)
        {
        }
    }

    [IntegrationService]
    private sealed class SampleIntegrationService
    {
        public Task ExecuteAsync()
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? Id => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        public string? UserName => "tester";
        public string? Name => "tester";
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId => null;
        public string? SessionId => null;
        public string[] Roles => [];
        public Claim? FindClaim(string claimType) => null;
        public Claim[] FindClaims(string claimType) => [];
        public Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => false;
        public Guid? Id => null;
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => NullDisposable.Instance;
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public TestOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => NullDisposable.Instance;
    }

    private sealed class TestAuditingManager : IAuditingManager
    {
        private readonly List<string> _order;
        private readonly AuditLogInfo _log;

        public TestAuditingManager(List<string> order)
        {
            _order = order;
            _log = new AuditLogInfo
            {
                ApplicationName = "tests",
                Category = "http.request",
                ExecutionTime = DateTime.UtcNow,
                RedactionApplied = true,
                RedactionVersion = 1
            };
        }

        public IAuditLogScope? Current { get; private set; }

        public IAuditLogSaveHandle BeginScope()
        {
            Current = new AuditLogScope(_log);
            return new TestSaveHandle(_order, this);
        }

        private sealed class TestSaveHandle : IAuditLogSaveHandle
        {
            private readonly List<string> _order;
            private readonly TestAuditingManager _manager;

            public TestSaveHandle(List<string> order, TestAuditingManager manager)
            {
                _order = order;
                _manager = manager;
            }

            public Task SaveAsync()
            {
                _order.Add("audit-save");
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                _manager.Current = null;
            }
        }
    }

    private sealed class TrackingAuditingManager : IAuditingManager
    {
        private readonly AuditLogInfo _log = new()
        {
            ApplicationName = "tests",
            Category = "http.request",
            ExecutionTime = DateTime.UtcNow,
            RedactionApplied = true,
            RedactionVersion = 1
        };

        public int SaveCount { get; private set; }

        public IAuditLogScope? Current { get; private set; }

        public IAuditLogSaveHandle BeginScope()
        {
            Current = new AuditLogScope(_log);
            return new TrackingSaveHandle(this);
        }

        private sealed class TrackingSaveHandle : IAuditLogSaveHandle
        {
            private readonly TrackingAuditingManager _manager;

            public TrackingSaveHandle(TrackingAuditingManager manager)
            {
                _manager = manager;
            }

            public Task SaveAsync()
            {
                _manager.SaveCount++;
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                _manager.Current = null;
            }
        }
    }

    private sealed class ConfigurableAuditingManager : IAuditingManager
    {
        private readonly bool _failOnSave;

        public ConfigurableAuditingManager(string? category, bool failOnSave)
        {
            _failOnSave = failOnSave;
            Log = new AuditLogInfo
            {
                ApplicationName = "tests",
                Category = category!,
                ExecutionTime = DateTime.UtcNow,
                RedactionApplied = true,
                RedactionVersion = 1
            };
        }

        public AuditLogInfo Log { get; }

        public IAuditLogScope? Current { get; private set; }

        public IAuditLogSaveHandle BeginScope()
        {
            Current = new AuditLogScope(Log);
            return new ConfigurableSaveHandle(this, _failOnSave);
        }

        private sealed class ConfigurableSaveHandle : IAuditLogSaveHandle
        {
            private readonly ConfigurableAuditingManager _manager;
            private readonly bool _failOnSave;

            public ConfigurableSaveHandle(ConfigurableAuditingManager manager, bool failOnSave)
            {
                _manager = manager;
                _failOnSave = failOnSave;
            }

            public Task SaveAsync()
            {
                if (_failOnSave)
                {
                    throw new InvalidOperationException("audit publish failed");
                }

                return Task.CompletedTask;
            }

            public void Dispose()
            {
                _manager.Current = null;
            }
        }
    }

    private sealed class SamplePayload
    {
        public string Public { get; set; } = string.Empty;

        [DisableAuditing]
        public string Secret { get; set; } = string.Empty;
    }

    private sealed class UnannotatedController
    {
        public Task PostAsync()
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
