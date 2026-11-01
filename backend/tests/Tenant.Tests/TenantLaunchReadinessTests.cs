using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Data.Filtering;
using Shared.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.Permissions.Authorization;
using Shared.Security;
using Shared.Threading;
using Shared.Uow.Options;
using Tenant.Application.Contracts.Tenants;
using Tenant.Application.Tenants;
using Tenant.Domain.Shared.Enums;
using Tenant.Domain.Tenants;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Tenant.Tests;

public sealed class TenantLaunchReadinessTests
{
    private static readonly string[] EvidenceCodes =
        ["invitation-created", "membership-active", "permissions-provisioned", "tenant-active"];

    [Fact]
    public async Task Complete_local_evidence_returns_ready_with_every_item_ready()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.GetCurrentAsync();

        Assert.Equal(fixture.TenantId, result.TenantId);
        Assert.Equal(TenantLaunchReadinessState.Ready, result.State);
        Assert.Empty(result.Blockers);
        Assert.Equal(EvidenceCodes, result.Evidence.Select(item => item.Code));
        Assert.Equal(["Identity", "Identity", "Permissions", "Tenant"], result.Evidence.Select(item => item.Owner));
        Assert.Equal(
            ["/workforce/invitations", "/workforce/users", "/access/delegation", "/"],
            result.Evidence.Select(item => item.DeepLink));
        Assert.All(result.Evidence, item =>
        {
            Assert.True(item.IsReady);
            Assert.NotNull(item.ObservedAtUtc);
        });
    }

    [Fact]
    public async Task Missing_initial_owner_request_blocks_every_ownership_evidence_item()
    {
        await using var fixture = await Fixture.CreateAsync(stage: OwnershipStage.None, assignOwner: false);

        var result = await fixture.Service.GetCurrentAsync();

        Assert.Equal(TenantLaunchReadinessState.Blocked, result.State);
        Assert.Equal(["invitation-created", "membership-active", "permissions-provisioned"], result.Blockers);
        Assert.True(Assert.Single(result.Evidence, item => item.Code == "tenant-active").IsReady);
        Assert.All(
            result.Evidence.Where(item => item.Code != "tenant-active"),
            item =>
            {
                Assert.False(item.IsReady);
                Assert.Null(item.ObservedAtUtc);
            });
    }

    [Fact]
    public async Task Pending_invitation_acceptance_blocks_membership_and_permissions()
    {
        await using var fixture = await Fixture.CreateAsync(stage: OwnershipStage.InvitationCreated, assignOwner: false);

        var result = await fixture.Service.GetCurrentAsync();

        Assert.Equal(TenantLaunchReadinessState.Blocked, result.State);
        Assert.Equal(["membership-active", "permissions-provisioned"], result.Blockers);
        Assert.True(Assert.Single(result.Evidence, item => item.Code == "invitation-created").IsReady);
    }

    [Fact]
    public async Task Completed_request_without_a_tenant_owner_keeps_membership_blocked()
    {
        await using var fixture = await Fixture.CreateAsync(assignOwner: false);

        var result = await fixture.Service.GetCurrentAsync();

        Assert.Equal(TenantLaunchReadinessState.Blocked, result.State);
        Assert.Equal(["membership-active"], result.Blockers);
        var evidence = Assert.Single(result.Evidence, item => item.Code == "membership-active");
        Assert.False(evidence.IsReady);
        Assert.Equal("/workforce/users", evidence.DeepLink);
    }

    [Fact]
    public async Task Unprovisioned_permissions_keep_launch_blocked()
    {
        await using var fixture = await Fixture.CreateAsync(stage: OwnershipStage.Completed);

        var result = await fixture.Service.GetCurrentAsync();

        Assert.Equal(TenantLaunchReadinessState.Blocked, result.State);
        Assert.Equal(["permissions-provisioned"], result.Blockers);
        var evidence = Assert.Single(result.Evidence, item => item.Code == "permissions-provisioned");
        Assert.Equal("Permissions", evidence.Owner);
        Assert.Equal("/access/delegation", evidence.DeepLink);
    }

    [Fact]
    public async Task Suspended_tenant_blocks_launch_even_with_complete_ownership_evidence()
    {
        await using var fixture = await Fixture.CreateAsync(suspendTenant: true);

        var result = await fixture.Service.GetCurrentAsync();

        Assert.Equal(TenantLaunchReadinessState.Blocked, result.State);
        Assert.Equal(["tenant-active"], result.Blockers);
        var evidence = Assert.Single(result.Evidence, item => item.Code == "tenant-active");
        Assert.False(evidence.IsReady);
        Assert.Equal("Tenant", evidence.Owner);
    }

    [Fact]
    public async Task Another_tenants_completed_ownership_never_satisfies_current_tenant_evidence()
    {
        await using var fixture = await Fixture.CreateAsync(stage: OwnershipStage.None, assignOwner: false);
        await fixture.SeedForeignTenantWithCompleteOwnershipAsync();

        var result = await fixture.Service.GetCurrentAsync();

        Assert.Equal(fixture.TenantId, result.TenantId);
        Assert.Equal(TenantLaunchReadinessState.Blocked, result.State);
        Assert.Equal(["invitation-created", "membership-active", "permissions-provisioned"], result.Blockers);
    }

    [Fact]
    public async Task Read_permission_is_rechecked_in_the_application_layer()
    {
        await using var fixture = await Fixture.CreateAsync(permissionGranted: false);

        await Assert.ThrowsAsync<SharedForbiddenException>(() => fixture.Service.GetCurrentAsync());
    }

    private enum OwnershipStage
    {
        None,
        InvitationCreated,
        Completed,
        PermissionsProvisioned
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(TenantDbContextApp db, TenantLaunchReadinessAppService service, Guid tenantId)
        {
            Db = db;
            Service = service;
            TenantId = tenantId;
        }

        public TenantDbContextApp Db { get; }
        public TenantLaunchReadinessAppService Service { get; }
        public Guid TenantId { get; }

        public static async Task<Fixture> CreateAsync(
            bool permissionGranted = true,
            OwnershipStage stage = OwnershipStage.PermissionsProvisioned,
            bool assignOwner = true,
            bool suspendTenant = false)
        {
            var tenantId = Guid.NewGuid();
            var currentTenant = new TestCurrentTenant(tenantId);
            var services = new ServiceCollection();
            services.AddSingleton<ICurrentTenant>(currentTenant);
            services.AddSingleton<IDataFilter, TestDataFilter>();
            services.AddSingleton<IAuditPropertySetter, NoopAuditPropertySetter>();
            services.AddSingleton<IAuditingManager, NoopAuditingManager>();
            services.AddSingleton<Shared.Uow.Abstractions.IUnitOfWorkManager, NoopUnitOfWorkManager>();
            services.AddSingleton<ICancellationTokenProvider, TestCancellationTokenProvider>();
            var provider = services.BuildServiceProvider();
            var options = new DbContextOptionsBuilder<TenantDbContextApp>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var db = new TenantDbContextApp(options) { ServiceProvider = provider };
            db.Initialize(new EfCoreDbContextInitializationContext(new TestUnitOfWork()));

            SeedTenant(db, tenantId, "Readiness Organization", stage, assignOwner, suspendTenant);
            await db.SaveChangesAsync();

            var service = new TenantLaunchReadinessAppService(
                currentTenant,
                new StaticDbContextProvider<TenantDbContextApp>(db),
                new PermissionChecker(permissionGranted),
                new PrincipalAccessor(),
                TimeProvider.System);
            return new Fixture(db, service, tenantId);
        }

        public async Task SeedForeignTenantWithCompleteOwnershipAsync()
        {
            SeedTenant(Db, Guid.NewGuid(), "Foreign Organization", OwnershipStage.PermissionsProvisioned,
                assignOwner: true, suspendTenant: false);
            await Db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static void SeedTenant(
            TenantDbContextApp db,
            Guid tenantId,
            string name,
            OwnershipStage stage,
            bool assignOwner,
            bool suspendTenant)
        {
            var now = DateTime.UtcNow;
            var ownerId = Guid.NewGuid();
            var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, name)
            {
                CreationTime = now
            };
            if (assignOwner)
            {
                tenant.SetOwner(ownerId, "Owner", "owner@example.test");
            }

            if (suspendTenant)
            {
                tenant.Deactivate();
            }

            db.Tenants.Add(tenant);
            if (stage == OwnershipStage.None)
            {
                return;
            }

            var ownership = new TenantOwnershipRequest(
                Guid.NewGuid(), tenantId, null, "owner@example.test", "owner", "initial-owner", null,
                TenantOwnershipRequestKind.InitialOwner, now.AddDays(1));
            ownership.AcknowledgeIdentityInvitation(Guid.NewGuid(), now);
            if (stage >= OwnershipStage.Completed)
            {
                Assert.True(ownership.Complete(ownerId, now.AddMinutes(1)));
            }

            if (stage >= OwnershipStage.PermissionsProvisioned)
            {
                Assert.True(ownership.AcknowledgePermissionsProvisioned(ownerId, now.AddMinutes(2)));
            }

            db.TenantOwnershipRequests.Add(ownership);
        }
    }

    private sealed class PermissionChecker(bool granted) : IPermissionChecker
    {
        public Task<bool> IsGrantedAsync(ClaimsPrincipal principal, string permissionName,
            CancellationToken cancellationToken = default) => Task.FromResult(granted);

        public Task<Dictionary<string, bool>> IsGrantedAsync(ClaimsPrincipal principal,
            IReadOnlyList<string> permissionNames, CancellationToken cancellationToken = default) =>
            Task.FromResult(permissionNames.ToDictionary(name => name, _ => granted));
    }

    private sealed class PrincipalAccessor : ICurrentPrincipalAccessor
    {
        public ClaimsPrincipal Principal { get; } = new(new ClaimsIdentity());
        public IDisposable Change(ClaimsPrincipal principal) => new DisposeAction(() => { });
    }

    private sealed class TestCurrentTenant(Guid? id) : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; } = id;
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new DisposeAction(() => Id = previous);
        }
    }

    private sealed class StaticDbContextProvider<TDbContext>(TDbContext db) : IDbContextProvider<TDbContext>
        where TDbContext : class, IEfCoreDbContext
    {
        public Task<TDbContext> GetDbContextAsync() => Task.FromResult(db);
    }

    private sealed class TestDataFilter : IDataFilter
    {
        public IDisposable Enable<TFilter>() where TFilter : class => new DisposeAction(() => { });
        public IDisposable Disable<TFilter>() where TFilter : class => new DisposeAction(() => { });
        public bool IsEnabled<TFilter>() where TFilter : class => true;
    }

    private sealed class DisposeAction(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class TestCancellationTokenProvider : ICancellationTokenProvider
    {
        public CancellationToken Token => CancellationToken.None;
    }

    private sealed class NoopAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object targetObject) { }
        public void SetDeletionProperties(object targetObject) { }
        public void SetModificationProperties(object targetObject) { }
        public void IncrementEntityVersionProperty(object entity) { }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;
        public IAuditLogSaveHandle BeginScope() => new NoopAuditScope();

        private sealed class NoopAuditScope : IAuditLogScope, IAuditLogSaveHandle
        {
            public AuditLogInfo Log { get; } = new();
            public Task SaveAsync() => Task.CompletedTask;
            public void Dispose() { }
        }
    }

    private sealed class NoopUnitOfWorkManager : Shared.Uow.Abstractions.IUnitOfWorkManager
    {
        public Shared.Uow.Abstractions.IUnitOfWork? Current => null;
        public Shared.Uow.Abstractions.IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Begin(bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }

    private sealed class TestUnitOfWork : Shared.Uow.Abstractions.IUnitOfWork
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public event EventHandler<Shared.Uow.Events.UnitOfWorkFailedEventArgs>? Failed { add { } remove { } }
        public event EventHandler<Shared.Uow.Events.UnitOfWorkEventArgs>? Disposed;
        public UnitOfWorkOptions Options { get; } = new();
        public Shared.Uow.Abstractions.IUnitOfWork? Outer => null;
        public bool IsReserved => false;
        public bool IsDisposed { get; private set; }
        public bool IsCompleted { get; private set; }
        public string? ReservationName => null;
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();
        public void SetOuter(Shared.Uow.Abstractions.IUnitOfWork? outer) { }
        public void Initialize(UnitOfWorkOptions options) { }
        public void Reserve(string reservationName) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(CancellationToken cancellationToken = default) { IsCompleted = true; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void OnCompleted(Func<Task> handler) { }
        public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => factory(key);
        public Shared.Uow.Abstractions.IDatabaseApi? FindDatabaseApi(string key) => null;
        public void AddDatabaseApi(string key, Shared.Uow.Abstractions.IDatabaseApi api) { }
        public Shared.Uow.Abstractions.IDatabaseApi GetOrAddDatabaseApi(string key, Func<Shared.Uow.Abstractions.IDatabaseApi> factory) => factory();
        public Shared.Uow.Abstractions.ITransactionApi? FindTransactionApi(string key) => null;
        public void AddTransactionApi(string key, Shared.Uow.Abstractions.ITransactionApi api) { }
        public Shared.Uow.Abstractions.ITransactionApi GetOrAddTransactionApi(string key, Func<Shared.Uow.Abstractions.ITransactionApi> factory) => factory();
        public void Dispose() { IsDisposed = true; Disposed?.Invoke(this, new Shared.Uow.Events.UnitOfWorkEventArgs(this)); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
