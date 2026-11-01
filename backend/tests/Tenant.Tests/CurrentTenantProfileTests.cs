using Tenant.Domain.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.BackgroundTasks.Workers;
using Shared.Caching.Abstractions;
using Shared.Data.Filtering;
using Shared.Events;
using Shared.Permissions.Definitions.Tenant;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore.Repositories;
using Shared.EventBus.Messaging;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.MultiTenancy;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.Threading;
using Shared.Uow.Options;
using Tenant.Application.Tenants;
using Tenant.Application.Contracts.Tenants;
using Tenant.Controllers.CurrentTenant;
using Tenant.Controllers.InternalTenants;
using Tenant.Controllers.Tenants;
using Shared.EventContracts;
using Tenant.Domain.Shared.Enums;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Tenant.Tests;

public partial class CurrentTenantProfileTests
{
    [Fact]
    public async Task GetCurrentProfileAsync_should_return_safe_current_tenant_profile()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(tenantId));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "north-org"));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: null!,
            tenantCacheManager: null!,
            currentTenant: new TestCurrentTenant(tenantId),
            eventBus: new NoopMessageSender(),
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor());

        var profile = await service.GetCurrentProfileAsync();

        Assert.Equal(tenantId, profile.Id);
        Assert.Equal("north-org", profile.Name);
        Assert.True(profile.IsActive);
    }

    [Fact]
    public async Task GetCurrentProfileAsync_should_fail_without_tenant_context()
    {
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: null!,
            tenantCacheManager: null!,
            currentTenant: new TestCurrentTenant(null),
            eventBus: new NoopMessageSender(),
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor());

        await Assert.ThrowsAsync<Shared.SharedValidationException>(() => service.GetCurrentProfileAsync());
    }

    [Fact]
    public async Task GetListAsync_should_filter_by_effective_lifecycle_status()
    {
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var active = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "active-org");
        var suspended = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "suspended-org");
        suspended.Deactivate();
        var legacyInactive = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "legacy-inactive-tenantwright");
        var provisioning = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "provisioning-org");
        provisioning.BeginProvisioning();
        dbContext.Tenants.AddRange(active, suspended, legacyInactive, provisioning);
        await dbContext.SaveChangesOnDbContextAsync(true);

        dbContext.Entry(legacyInactive).Property(x => x.IsActive).CurrentValue = false;
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = CreateLifecycleService(dbContext);
        var result = await service.GetListAsync(new GetTenantsInput
        {
            LifecycleStatuses = [TenantLifecycleStatus.Suspended],
            MaxResultCount = 50
        });

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Items, item => item.Id == legacyInactive.Id);
        Assert.Contains(result.Items, item => item.Id == suspended.Id);
        Assert.DoesNotContain(result.Items, item => item.Id == active.Id || item.Id == provisioning.Id);
        Assert.All(result.Items, item => Assert.Equal(TenantLifecycleStatus.Suspended, item.LifecycleStatus));
    }

    [Fact]
    public void CurrentTenantController_should_expose_read_only_profile_route()
    {
        var authorize = typeof(CurrentTenantController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        var route = typeof(CurrentTenantController).GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Single();
        var action = typeof(CurrentTenantController).GetMethod(nameof(CurrentTenantController.GetProfileAsync));

        Assert.Equal(TenantPermissions.Profile.Read, authorize.Policy);
        Assert.Equal("api/tenant/current", route.Template);
        Assert.NotNull(action);
        Assert.NotNull(action!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
            .Cast<HttpGetAttribute>()
            .SingleOrDefault(x => x.Template == "profile"));
        Assert.Empty(typeof(CurrentTenantController).GetMethods()
            .Where(x => x.DeclaringType == typeof(CurrentTenantController))
            .SelectMany(x => x.GetCustomAttributes(inherit: true))
            .OfType<HttpPostAttribute>());
        var updateAction = typeof(CurrentTenantController).GetMethod(nameof(CurrentTenantController.UpdateOrganizationProfileAsync));
        Assert.NotNull(updateAction);
        Assert.Contains(updateAction!.GetCustomAttributes(typeof(HttpPutAttribute), inherit: true).Cast<HttpPutAttribute>(), x => x.Template == "organization-profile");
        Assert.Contains(updateAction.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>(), x => x.Policy == TenantPermissions.Profile.Update);
        var organizationReadAction = typeof(CurrentTenantController).GetMethod(nameof(CurrentTenantController.GetOrganizationProfileAsync));
        Assert.NotNull(organizationReadAction);
        Assert.Contains(organizationReadAction!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).Cast<HttpGetAttribute>(), x => x.Template == "organization-profile");
        Assert.Empty(typeof(CurrentTenantController).GetMethods()
            .Where(x => x.DeclaringType == typeof(CurrentTenantController))
            .SelectMany(x => x.GetCustomAttributes(inherit: true))
            .OfType<HttpDeleteAttribute>());
    }

    [Fact]
    public async Task Current_owner_should_read_organization_profile_and_reject_stale_update()
    {
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(tenantId);
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "north-org");
        tenant.SetOwner(ownerUserId);
        tenant.SetOrganizationProfile("Owner", "owner@example.test", null, null, null, "Pune", "Maharashtra", "411001");
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: null!,
            tenantCacheManager: null!,
            currentTenant: currentTenant,
            eventBus: new NoopMessageSender(),
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor(),
            currentUser: new TestCurrentUser(ownerUserId, tenantId));

        var profile = await service.GetCurrentOrganizationProfileAsync();

        Assert.Equal(tenantId, profile.TenantId);
        Assert.Equal("owner@example.test", profile.ContactEmail);
        Assert.False(string.IsNullOrWhiteSpace(profile.ConcurrencyStamp));

        var updated = await service.UpdateCurrentOrganizationProfileAsync(
            new Tenant.Application.Contracts.Tenants.TenantOrganizationProfileUpdateInput
            {
                ContactName = "Updated Owner",
                ContactEmail = profile.ContactEmail,
                City = "Pune",
                StateOrUnionTerritory = "Maharashtra",
                PinCode = "411001",
                ConcurrencyStamp = profile.ConcurrencyStamp
            });

        Assert.Equal("Updated Owner", updated.ContactName);
        Assert.False(string.IsNullOrWhiteSpace(updated.ConcurrencyStamp));

        var error = await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            service.UpdateCurrentOrganizationProfileAsync(new Tenant.Application.Contracts.Tenants.TenantOrganizationProfileUpdateInput
            {
                ContactName = "Overwritten",
                ConcurrencyStamp = "stale-stamp"
            }));

        Assert.Contains("modified by someone else", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Updated Owner", (await service.GetCurrentOrganizationProfileAsync()).ContactName);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetLegalComplianceProfileAsync(tenantId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetDataGovernanceProfileAsync(tenantId));
    }

    [Fact]
    public async Task UpdateAsync_should_publish_tenant_deactivated_event_on_active_to_inactive_transition()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        var eventBus = new RecordingMessageSender();
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "north-org"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: new TenantManager(CreateTenantRepository(dbContext)),
            tenantCacheManager: CreateTenantCacheManager(dbContext),
            currentTenant: currentTenant,
            eventBus: eventBus,
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor());

        var updated = await service.UpdateAsync(tenantId, new Tenant.Application.Contracts.Tenants.TenantUpdateDto
        {
            Name = "north-org",
            IsActive = false
        });

        Assert.False(updated.IsActive);
        var published = Assert.Single(eventBus.Published.OfType<TenantDeactivatedEto>());
        Assert.Equal(tenantId, published.TenantId);
        Assert.Equal("north-org", published.TenantName);
        var records = eventBus.PublishRecords;
        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.True(record.OnUnitOfWorkComplete);
            Assert.True(record.UseOutbox);
        });
        var lifecycle = Assert.Single(eventBus.Published.OfType<TenantLifecycleChangedEto>());
        Assert.Equal(TenantLifecycleStatusEto.Active, lifecycle.PreviousStatus);
        Assert.Equal(TenantLifecycleStatusEto.Suspended, lifecycle.CurrentStatus);
        Assert.Equal("legacy-update", lifecycle.ReasonCode);
    }

    [Fact]
    public async Task UpdateAsync_should_not_publish_tenant_deactivated_event_when_tenant_already_inactive()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        var eventBus = new RecordingMessageSender();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "south-org");
        tenant.Deactivate();
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: new TenantManager(CreateTenantRepository(dbContext)),
            tenantCacheManager: CreateTenantCacheManager(dbContext),
            currentTenant: currentTenant,
            eventBus: eventBus,
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor());

        var updated = await service.UpdateAsync(tenantId, new Tenant.Application.Contracts.Tenants.TenantUpdateDto
        {
            Name = "south-org",
            IsActive = false
        });

        Assert.False(updated.IsActive);
        Assert.Empty(eventBus.Published.OfType<TenantDeactivatedEto>());
    }

    [Fact]
    public async Task DeleteAsync_should_publish_tenant_deactivated_event_for_active_tenant()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        var eventBus = new RecordingMessageSender();
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "delete-org"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: new TenantManager(CreateTenantRepository(dbContext)),
            tenantCacheManager: CreateTenantCacheManager(dbContext),
            currentTenant: currentTenant,
            eventBus: eventBus,
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor());

        await AddClosedClosureRequestAsync(dbContext, tenantId);
        await service.DeleteAsync(tenantId);

        var published = Assert.Single(eventBus.Published.OfType<TenantDeactivatedEto>());
        Assert.Equal(tenantId, published.TenantId);
        Assert.Equal("delete-org", published.TenantName);
        var record = Assert.Single(eventBus.PublishRecords.Where(x => x.EventData is TenantDeactivatedEto));
        Assert.True(record.OnUnitOfWorkComplete);
        Assert.True(record.UseOutbox);
    }

    [Fact]
    public async Task DeleteProvisioningAsync_should_hard_delete_without_deactivation_event()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        var eventBus = new RecordingMessageSender();
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "failed-provisioning"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: new TenantManager(CreateTenantRepository(dbContext)),
            tenantCacheManager: CreateTenantCacheManager(dbContext),
            currentTenant: currentTenant,
            eventBus: eventBus,
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor());

        await service.DeleteProvisioningAsync(tenantId);

        Assert.False(await dbContext.Tenants.AnyAsync(x => x.Id == tenantId));
        Assert.Empty(eventBus.Published.OfType<TenantDeactivatedEto>());
    }

    [Fact]
    public async Task DeleteAsync_should_tombstone_instead_of_remove_and_resolver_should_fail_closed_even_with_host_fallback()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "tombstone-me"));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var byIdCache = new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string>();
        var byNameCache = new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantByNameCacheItem, string>();
        var cacheManager = new TenantCacheManager(
            CreateTenantRepository(dbContext),
            byIdCache,
            byNameCache,
            new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantListCacheItem, string>(),
            new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantListStampCacheItem, string>(),
            new NoopTenantConnectionStringProtector(),
            new RecordingDistributedLock(),
            dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            Options.Create(new TenantCacheOptions()),
            NullLogger<TenantCacheManager>.Instance);

        // Seed the by-name entry the way UpsertTenantAsync would, so we can prove
        // DeleteAsync's tombstone removes it (login-by-name must not resolve a
        // soft-deleted tenant).
        await cacheManager.UpsertTenantAsync(tenantId);
        Assert.NotNull(byNameCache.Get("TOMBSTONE-ME"));

        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: new TenantManager(CreateTenantRepository(dbContext)),
            tenantCacheManager: cacheManager,
            currentTenant: currentTenant,
            eventBus: new NoopMessageSender(),
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor());

        await AddClosedClosureRequestAsync(dbContext, tenantId);
        await service.DeleteAsync(tenantId);

        // By-id: positively-known inactive, not removed.
        var tombstone = byIdCache.Get(tenantId.ToString("D"));
        Assert.NotNull(tombstone);
        Assert.True(tombstone!.Exists);
        Assert.False(tombstone.IsActive);

        // By-name: removed, so login-by-name cannot resolve the tenant.
        Assert.Null(byNameCache.Get("TOMBSTONE-ME"));

        // The resolver's fail-closed hard-stop fires even when host fallback is
        // allowed, because the cache holds Exists=true/IsActive=false, not a
        // cold-miss.
        var tenantStore = new Shared.MultiTenancy.Caching.CachedTenantStore(
            byIdCache,
            byNameCache,
            new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantListCacheItem, string>(),
            new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantListStampCacheItem, string>());
        var dbOptions = new SharedDbConnectionOptions();
        dbOptions.ConnectionStrings.Default = "HostDefault";
        var resolver = new Shared.EntityFrameworkCore.ConnectionStrings.TenantConnectionStringResolver(
            currentTenant: new TenantResolverTestCurrentTenant(tenantId),
            tenantStore: tenantStore,
            tenantConnectionStringsCache: new TenantResolverTestConnectionStringsCache(),
            tenantConnectionStringProtector: new NoopTenantConnectionStringProtector(),
            options: new TenantResolverTestOptionsMonitor<SharedDbConnectionOptions>(dbOptions),
            resolverOptions: new TenantResolverTestOptionsMonitor<SharedConnectionStringResolverOptions>(
                new SharedConnectionStringResolverOptions { AllowHostFallbackForTenant = true }),
            logger: NullLogger<Shared.EntityFrameworkCore.ConnectionStrings.TenantConnectionStringResolver>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("Default"));
    }

    private sealed class TenantResolverTestCurrentTenant : ICurrentTenant
    {
        public TenantResolverTestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; }
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => new DisposeAction(() => { });
    }

    private sealed class TenantResolverTestConnectionStringsCache : Shared.EntityFrameworkCore.ConnectionStrings.ITenantConnectionStringsCache
    {
        private readonly Dictionary<Guid, Shared.EntityFrameworkCore.ConnectionStrings.TenantConnectionStringsCacheItem> _cache = new();

        public async Task<Shared.EntityFrameworkCore.ConnectionStrings.TenantConnectionStringsCacheItem> GetOrAddAsync(
            Guid tenantId,
            Func<Task<Shared.EntityFrameworkCore.ConnectionStrings.TenantConnectionStringsCacheItem>> factory,
            CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(tenantId, out var existing))
            {
                return existing;
            }

            var created = await factory();
            _cache[tenantId] = created;
            return created;
        }

        public Task RemoveAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            _cache.Remove(tenantId);
            return Task.CompletedTask;
        }
    }

    private sealed class TenantResolverTestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class, new()
    {
        public TenantResolverTestOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public async Task RejectClosureAsync_should_persist_decision_without_changing_lifecycle_or_publishing_events()
    {
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var request = new global::Tenant.Domain.Tenants.TenantClosureRequest(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "owner-request", "close");
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "reject-closure");
        dbContext.Tenants.Add(tenant);
        dbContext.TenantClosureRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var eventBus = new RecordingMessageSender();
        var service = CreateLifecycleService(
            dbContext,
            eventBus,
            new TestCurrentUser(actorId, tenantId));

        var result = await service.RejectClosureAsync(
            tenantId,
            request.Id,
            new TenantClosureActionInput
            {
                ReasonCode = "impact-not-approved",
                Note = "Keep organization active"
            });

        Assert.Equal(TenantClosureStatus.Rejected, result.Status);
        Assert.Equal(actorId, result.RejectedByUserId);
        Assert.Equal("impact-not-approved", result.DecisionReasonCode);
        Assert.Equal("Keep organization active", result.DecisionNote);
        Assert.NotNull(result.RejectedAtUtc);
        Assert.Equal(TenantLifecycleStatus.Active, tenant.GetEffectiveLifecycleStatus());
        Assert.Empty(eventBus.Published);

        var replacement = await service.RequestClosureAsync(
            tenantId,
            new TenantClosureRequestInput { ReasonCode = "new-owner-request" });
        Assert.Equal(TenantClosureStatus.Requested, replacement.Status);
    }

    [Fact]
    public async Task GetCurrentClosureRequestAsync_should_return_latest_safe_status_only_to_persisted_owner()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(tenantId);
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "closure-status");
        tenant.SetOwner(ownerId);
        var older = new global::Tenant.Domain.Tenants.TenantClosureRequest(
            Guid.NewGuid(), tenantId, ownerId, "older", null)
        {
            CreationTime = DateTime.UtcNow.AddDays(-1)
        };
        var latest = new global::Tenant.Domain.Tenants.TenantClosureRequest(
            Guid.NewGuid(), tenantId, ownerId, "latest", "latest note")
        {
            CreationTime = DateTime.UtcNow
        };
        dbContext.Tenants.Add(tenant);
        dbContext.TenantClosureRequests.AddRange(older, latest);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = CreateLifecycleService(
            dbContext,
            currentUser: new TestCurrentUser(ownerId, tenantId),
            currentTenant: currentTenant);
        var result = await service.GetCurrentClosureRequestAsync();

        Assert.NotNull(result);
        Assert.Equal(latest.Id, result!.RequestId);
        Assert.Equal("latest", result.ReasonCode);
        var properties = typeof(TenantClosureStatusDto).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(nameof(TenantClosureRequestDto.RequestedByUserId), properties);
        Assert.DoesNotContain(nameof(TenantClosureRequestDto.ApprovedByUserId), properties);
        Assert.DoesNotContain(nameof(TenantClosureRequestDto.RejectedByUserId), properties);

        var nonOwnerService = CreateLifecycleService(
            dbContext,
            currentUser: new TestCurrentUser(Guid.NewGuid(), tenantId),
            currentTenant: currentTenant);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nonOwnerService.GetCurrentClosureRequestAsync());
    }

    [Fact]
    public async Task GetCurrentClosureRequestAsync_should_return_null_when_owner_has_no_request()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(tenantId);
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "no-closure-status");
        tenant.SetOwner(ownerId);
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(
            dbContext,
            currentUser: new TestCurrentUser(ownerId, tenantId),
            currentTenant: currentTenant);

        Assert.Null(await service.GetCurrentClosureRequestAsync());
    }

    [Fact]
    public void Current_closure_status_route_should_be_read_only_and_use_request_permission()
    {
        var method = typeof(CurrentTenantController).GetMethod(nameof(CurrentTenantController.GetClosureRequestAsync));

        Assert.NotNull(method);
        Assert.Contains(method!.GetCustomAttributes(typeof(HttpGetAttribute), true).Cast<HttpGetAttribute>(),
            x => x.Template == "closure-request");
        Assert.Contains(method.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(),
            x => x.Policy == TenantPermissions.Closure.Request);
        Assert.Empty(method.GetCustomAttributes(typeof(AuditedAttribute), true));
    }

    [Fact]
    public void RejectClosure_endpoint_should_use_existing_manage_permission_and_auditing()
    {
        var method = typeof(TenantsController).GetMethod(nameof(TenantsController.RejectClosureAsync));

        Assert.NotNull(method);
        Assert.Contains(method!.GetCustomAttributes(typeof(HttpPostAttribute), true).Cast<HttpPostAttribute>(),
            x => x.Template == "{id:guid}/closure-requests/{requestId:guid}/reject");
        Assert.Contains(method.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(),
            x => x.Policy == TenantPermissions.Closure.Manage);
        Assert.NotEmpty(method.GetCustomAttributes(typeof(AuditedAttribute), true));
    }

    [Fact]
    public async Task ArchiveAsync_should_require_latest_closed_closure_and_record_decommissioning()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "archive-ready");
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var eventBus = new RecordingMessageSender();
        var service = CreateLifecycleService(dbContext, eventBus);
        var input = new TenantArchiveInput
        {
            TenantNameConfirmation = "archive-ready",
            ReasonCode = "retention-approved",
            Note = "Closure completed"
        };

        await Assert.ThrowsAsync<SharedValidationException>(() => service.ArchiveAsync(tenantId, input));
        var closure = await AddClosedClosureRequestAsync(dbContext, tenantId);

        var result = await service.ArchiveAsync(tenantId, input);

        Assert.Equal(closure.Id, result.ClosureRequestId);
        Assert.Equal(TenantLifecycleStatus.Active, result.PreviousStatus);
        Assert.Equal(TenantLifecycleStatus.Decommissioned, result.CurrentStatus);
        Assert.False(result.UsedOverride);
        Assert.False(result.WasAlreadyArchived);
        Assert.True(tenant.IsDeleted);
        Assert.Equal(TenantLifecycleStatus.Decommissioned, tenant.GetEffectiveLifecycleStatus());
        var transition = await dbContext.TenantLifecycleTransitions.SingleAsync(x => x.TenantId == tenantId && x.ReasonCode == "retention-approved");
        Assert.Equal(TenantLifecycleStatus.Decommissioned, transition.CurrentStatus);
        Assert.Contains(eventBus.Published, x => x is TenantDeactivatedEto);
        Assert.Contains(eventBus.Published, x => x is TenantLifecycleChangedEto changed && changed.CurrentStatus == TenantLifecycleStatusEto.Decommissioned);

        var replay = await service.ArchiveAsync(tenantId, new TenantArchiveInput());
        Assert.True(replay.WasAlreadyArchived);
    }

    [Fact]
    public async Task ArchiveOverrideAsync_should_require_name_and_allow_emergency_without_closure()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "emergency-archive");
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        await Assert.ThrowsAsync<SharedValidationException>(() => service.ArchiveOverrideAsync(
            tenantId,
            new TenantArchiveInput
            {
                TenantNameConfirmation = "wrong",
                ReasonCode = "security-incident"
            }));

        var result = await service.ArchiveOverrideAsync(
            tenantId,
            new TenantArchiveInput
            {
                TenantNameConfirmation = "emergency-archive",
                ReasonCode = "security-incident",
                Note = "Emergency isolation"
            });

        Assert.True(result.UsedOverride);
        Assert.Null(result.ClosureRequestId);
        Assert.True(tenant.IsDeleted);
        Assert.Equal(TenantLifecycleStatus.Decommissioned, tenant.GetEffectiveLifecycleStatus());
    }

    [Fact]
    public async Task Legacy_delete_should_not_bypass_closed_closure_gate()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "legacy-delete-gate");
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        await Assert.ThrowsAsync<SharedValidationException>(() => service.DeleteAsync(tenantId));
        await AddClosedClosureRequestAsync(dbContext, tenantId);
        await service.DeleteAsync(tenantId);

        Assert.True(tenant.IsDeleted);
        Assert.Equal(TenantLifecycleStatus.Decommissioned, tenant.GetEffectiveLifecycleStatus());
    }

    private static async Task<global::Tenant.Domain.Tenants.TenantClosureRequest> AddClosedClosureRequestAsync(
        TenantDbContextApp dbContext,
        Guid tenantId)
    {
        var request = new global::Tenant.Domain.Tenants.TenantClosureRequest(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            "test-closure",
            null)
        {
            CreationTime = DateTime.UtcNow
        };
        request.Approve(Guid.NewGuid(), DateTime.UtcNow);
        request.Close(DateTime.UtcNow);
        dbContext.TenantClosureRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        return request;
    }

    private static TenantAppService CreateLifecycleService(
        TenantDbContextApp dbContext,
        ITenantWrightMessageSender? eventBus = null,
        ICurrentUser? currentUser = null,
        ICurrentTenant? currentTenant = null)
    {
        return new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: new TenantManager(CreateTenantRepository(dbContext)),
            tenantCacheManager: CreateTenantCacheManager(dbContext),
            currentTenant: currentTenant ?? new TestCurrentTenant(null),
            eventBus: eventBus ?? new NoopMessageSender(),
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor(),
            currentUser: currentUser);
    }

    [Fact]
    public async Task RestoreAsync_should_bring_back_tenant_deactivated()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "restore-me"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        await AddClosedClosureRequestAsync(dbContext, tenantId);
        await service.DeleteAsync(tenantId);
        await service.RestoreAsync(tenantId, new Tenant.Application.Contracts.Tenants.TenantRestoreInput());

        var restored = await dbContext.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId);
        Assert.NotNull(restored);
        Assert.False(restored!.IsDeleted);
        Assert.False(restored.IsActive); // fail-closed: restored inactive
    }

    [Fact]
    public async Task RestoreAsync_should_block_when_active_tenant_holds_the_name()
    {
        var deletedId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(deletedId, "acme"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        await AddClosedClosureRequestAsync(dbContext, deletedId);
        await service.DeleteAsync(deletedId);
        // A new active tenant reuses the freed name (allowed once soft-deleted).
        await service.CreateAsync(new Tenant.Application.Contracts.Tenants.TenantCreateDto { Name = "acme" });

        await Assert.ThrowsAsync<TenantNameAlreadyExistsException>(() => service.RestoreAsync(deletedId, new Tenant.Application.Contracts.Tenants.TenantRestoreInput()));
    }

    [Fact]
    public async Task RestoreAsync_should_be_idempotent_when_tenant_not_deleted()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "still-here"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        await service.RestoreAsync(tenantId, new Tenant.Application.Contracts.Tenants.TenantRestoreInput()); // no-op, must not throw

        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId);
        Assert.NotNull(tenant);
        Assert.False(tenant!.IsDeleted);
        Assert.True(tenant.IsActive);
    }

    [Fact]
    public async Task RestoreAsync_should_throw_not_found_when_tenant_missing()
    {
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var service = CreateLifecycleService(dbContext);

        await Assert.ThrowsAsync<Shared.Domain.Entities.EntityNotFoundException>(
            () => service.RestoreAsync(Guid.NewGuid(), new Tenant.Application.Contracts.Tenants.TenantRestoreInput()));
    }

    [Fact]
    public async Task RestoreAsync_should_record_a_lifecycle_transition_with_reason()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "restore-reason"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        await AddClosedClosureRequestAsync(dbContext, tenantId);
        await service.DeleteAsync(tenantId);
        await service.RestoreAsync(tenantId, new Tenant.Application.Contracts.Tenants.TenantRestoreInput
        {
            ReasonCode = "ops-recovery",
            Note = "restored after review"
        });

        var transition = await dbContext.TenantLifecycleTransitions
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ReasonCode == "ops-recovery");
        Assert.NotNull(transition);
        Assert.Equal("ops-recovery", transition!.ReasonCode);
        Assert.Equal(global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended, transition.CurrentStatus);
    }

    [Fact]
    public async Task GetDetailAsync_should_return_deleted_tenant_when_requested()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "detail-me"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);
        await AddClosedClosureRequestAsync(dbContext, tenantId);
        await service.DeleteAsync(tenantId);

        // Excluded by default...
        await Assert.ThrowsAsync<Shared.Domain.Entities.EntityNotFoundException>(
            () => service.GetDetailAsync(tenantId, includeDeleted: false));

        // ...but visible with the deleted-detail read.
        var detail = await service.GetDetailAsync(tenantId, includeDeleted: true);
        Assert.Equal(tenantId, detail.Id);
        Assert.True(detail.IsDeleted);
        Assert.False(string.IsNullOrWhiteSpace(detail.ConcurrencyStamp));
        Assert.Equal(
            Shared.Data.ConnectionStrings.TenantWrightDatabaseCatalog.TenantDedicatable,
            detail.Databases.Select(database => database.Name));
        Assert.All(detail.Databases, database => Assert.False(database.IsDedicated));
    }

    [Fact]
    public async Task GetHistoryAsync_should_merge_all_sources_newest_first()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "history-merge"));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        dbContext.TenantLifecycleTransitions.Add(new global::Tenant.Domain.Tenants.TenantLifecycleTransition(
            Guid.NewGuid(), tenantId,
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active,
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended,
            "review", null)
        { CreationTime = t0.AddMinutes(1) });
        dbContext.Set<global::Tenant.Domain.Tenants.TenantOwnershipRequest>().Add(
            new global::Tenant.Domain.Tenants.TenantOwnershipRequest(
                Guid.NewGuid(), tenantId, null, "owner@example.com", null, "transfer", null,
                global::Tenant.Domain.Shared.Enums.TenantOwnershipRequestKind.Transfer)
            { CreationTime = t0.AddMinutes(2) });
        dbContext.Set<global::Tenant.Domain.Tenants.TenantClosureRequest>().Add(
            new global::Tenant.Domain.Tenants.TenantClosureRequest(
                Guid.NewGuid(), tenantId, Guid.NewGuid(), "closing", null)
            { CreationTime = t0.AddMinutes(3) });
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var result = await service.GetHistoryAsync(tenantId,
            new Tenant.Application.Contracts.Tenants.GetTenantHistoryInput());

        Assert.Equal(3, result.TotalCount);
        Assert.Collection(result.Items,
            e => Assert.Equal(Tenant.Application.Contracts.Tenants.TenantHistoryEntryType.ClosureRequest, e.EntryType),
            e => Assert.Equal(Tenant.Application.Contracts.Tenants.TenantHistoryEntryType.OwnershipRequest, e.EntryType),
            e => Assert.Equal(Tenant.Application.Contracts.Tenants.TenantHistoryEntryType.LifecycleTransition, e.EntryType));
    }

    [Fact]
    public async Task GetHistoryAsync_should_filter_by_entry_type()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "history-filter"));
        dbContext.TenantLifecycleTransitions.Add(new global::Tenant.Domain.Tenants.TenantLifecycleTransition(
            Guid.NewGuid(), tenantId,
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active,
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended,
            "review", null));
        dbContext.Set<global::Tenant.Domain.Tenants.TenantOwnershipRequest>().Add(
            new global::Tenant.Domain.Tenants.TenantOwnershipRequest(
                Guid.NewGuid(), tenantId, null, "owner@example.com", null, "transfer", null,
                global::Tenant.Domain.Shared.Enums.TenantOwnershipRequestKind.Transfer));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var onlyOwnership = await service.GetHistoryAsync(tenantId,
            new Tenant.Application.Contracts.Tenants.GetTenantHistoryInput
            {
                EntryType = Tenant.Application.Contracts.Tenants.TenantHistoryEntryType.OwnershipRequest
            });

        Assert.Equal(1, onlyOwnership.TotalCount);
        Assert.Equal(Tenant.Application.Contracts.Tenants.TenantHistoryEntryType.OwnershipRequest,
            Assert.Single(onlyOwnership.Items).EntryType);
    }

    [Fact]
    public async Task GetHistoryCursorAsync_should_page_the_database_backed_merged_history()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "history-cursor"));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        dbContext.TenantLifecycleTransitions.Add(new global::Tenant.Domain.Tenants.TenantLifecycleTransition(
            Guid.NewGuid(), tenantId,
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active,
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended,
            "review", null)
        { CreationTime = t0.AddMinutes(1) });
        dbContext.Set<global::Tenant.Domain.Tenants.TenantOwnershipRequest>().Add(
            new global::Tenant.Domain.Tenants.TenantOwnershipRequest(
                Guid.NewGuid(), tenantId, null, "owner@example.com", null, "transfer", null,
                global::Tenant.Domain.Shared.Enums.TenantOwnershipRequestKind.Transfer)
            { CreationTime = t0.AddMinutes(2) });
        dbContext.Set<global::Tenant.Domain.Tenants.TenantClosureRequest>().Add(
            new global::Tenant.Domain.Tenants.TenantClosureRequest(
                Guid.NewGuid(), tenantId, Guid.NewGuid(), "closing", null)
            { CreationTime = t0.AddMinutes(3) });
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var firstPage = await service.GetHistoryCursorAsync(tenantId,
            new Tenant.Application.Contracts.Tenants.GetTenantHistoryCursorInput { MaxResultCount = 2 });
        var secondPage = await service.GetHistoryCursorAsync(tenantId,
            new Tenant.Application.Contracts.Tenants.GetTenantHistoryCursorInput
            {
                MaxResultCount = 2,
                Cursor = firstPage.NextCursor
            });

        Assert.True(firstPage.HasMore);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextCursor);
        Assert.False(secondPage.HasMore);
        Assert.Single(secondPage.Items);
        Assert.Equal(
            new[]
            {
                global::Tenant.Application.Contracts.Tenants.TenantHistoryEntryType.ClosureRequest,
                global::Tenant.Application.Contracts.Tenants.TenantHistoryEntryType.OwnershipRequest,
                global::Tenant.Application.Contracts.Tenants.TenantHistoryEntryType.LifecycleTransition
            },
            firstPage.Items.Concat(secondPage.Items).Select(item => item.EntryType));
    }

    [Fact]
    public async Task GetOperationalSummaryAsync_should_report_connection_and_active_request_counts()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "ops-summary"));
        dbContext.Set<global::Tenant.Domain.Tenants.TenantOwnershipRequest>().Add(
            new global::Tenant.Domain.Tenants.TenantOwnershipRequest(
                Guid.NewGuid(), tenantId, null, "owner@example.com", null, "transfer", null,
                global::Tenant.Domain.Shared.Enums.TenantOwnershipRequestKind.Transfer));
        dbContext.Set<global::Tenant.Domain.Tenants.TenantClosureRequest>().Add(
            new global::Tenant.Domain.Tenants.TenantClosureRequest(
                Guid.NewGuid(), tenantId, Guid.NewGuid(), "closing", null));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);
        var tenantWithDatabase = await dbContext.Tenants
            .Include(x => x.ConnectionStrings)
            .SingleAsync(x => x.Id == tenantId);
        tenantWithDatabase.SetConnectionString(
            Shared.Data.ConnectionStrings.TenantWrightDatabaseCatalog.Notifications,
            "Host=localhost;Database=TenantDb_ops;Username=u;Password=p");
        await dbContext.SaveChangesOnDbContextAsync(true);

        var summary = await service.GetOperationalSummaryAsync(tenantId);

        Assert.Equal(tenantId, summary.TenantId);
        Assert.Equal(1, summary.DedicatedDatabaseCount);
        Assert.Equal(1, summary.ActiveOwnershipRequestCount);
        Assert.Equal(1, summary.ActiveClosureRequestCount);
    }

    [Fact]
    public async Task GetActionImpactAsync_should_keep_the_local_preview_when_enrichment_is_unavailable()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "impact-preview"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var impact = await service.GetActionImpactAsync(
            tenantId,
            Tenant.Application.Contracts.Tenants.TenantActionImpactAction.Suspend);

        Assert.Equal(Tenant.Application.Contracts.Tenants.TenantActionImpactAction.Suspend, impact.Action);
        Assert.Equal(global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active, impact.CurrentStatus);
        Assert.Equal(global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended, impact.TargetStatus);
        Assert.Equal(Tenant.Application.Contracts.Tenants.TenantImpactDependencyStatus.Unavailable, impact.IdentityStatus);
        Assert.Equal(Tenant.Application.Contracts.Tenants.TenantImpactDependencyStatus.Unavailable, impact.NotificationsStatus);
    }

    [Fact]
    public async Task GetActionImpactAsync_should_preview_activation_for_a_suspended_tenant()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "activation-preview");
        tenant.TransitionTo(global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended);
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var impact = await service.GetActionImpactAsync(
            tenantId,
            Tenant.Application.Contracts.Tenants.TenantActionImpactAction.Activate);

        Assert.Equal(Tenant.Application.Contracts.Tenants.TenantActionImpactAction.Activate, impact.Action);
        Assert.Equal(global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended, impact.CurrentStatus);
        Assert.Equal(global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active, impact.TargetStatus);
        Assert.Contains("regain access", impact.Consequences.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetListAsync_should_include_deleted_only_when_requested()
    {
        var activeId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(activeId, "alpha"));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(deletedId, "bravo"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);
        await AddClosedClosureRequestAsync(dbContext, deletedId);
        await service.DeleteAsync(deletedId);

        var excluded = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput { IncludeDeleted = false });
        Assert.Equal(1, excluded.TotalCount);
        Assert.DoesNotContain(excluded.Items, x => x.Id == deletedId);

        var included = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput { IncludeDeleted = true });
        Assert.Equal(2, included.TotalCount);
        Assert.Contains(included.Items, x => x.Id == deletedId && x.IsDeleted);
    }

    [Fact]
    public async Task GetListAsync_search_should_be_case_insensitive()
    {
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "Acme Health"));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "Other Clinic"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var result = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput { Filter = "acme" });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Acme Health", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task GetSelectionOptionsAsync_should_return_bounded_cached_tenant_summaries()
    {
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var acmeId = Guid.NewGuid();
        var acme = new global::Tenant.Domain.Tenants.Tenant(acmeId, "Acme Health");
        var suspended = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "Acme Legacy");
        suspended.Deactivate();
        dbContext.Tenants.AddRange(acme, suspended);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var initial = await service.GetSelectionOptionsAsync(new GetTenantSelectionOptionsInput
        {
            Search = "acme",
            MaxResultCount = 1
        });

        var selected = Assert.Single(initial);
        Assert.Equal(acmeId, selected.Id);
        Assert.Equal("Acme Health", selected.Name);
        Assert.True(selected.IsActive);

        acme.SetName("Renamed after cache warmup");
        await dbContext.SaveChangesOnDbContextAsync(true);

        var cached = await service.GetSelectionOptionsAsync(new GetTenantSelectionOptionsInput
        {
            Search = acmeId.ToString("D"),
            MaxResultCount = 200
        });

        Assert.Equal("Acme Health", Assert.Single(cached).Name);

        var activeOnly = await service.GetSelectionOptionsAsync(new GetTenantSelectionOptionsInput
        {
            Search = "legacy",
            IncludeInactive = false
        });
        Assert.Empty(activeOnly);
    }

    [Fact]
    public async Task GetSelectionOptionsAsync_should_reject_ambient_tenant_context()
    {
        var ambientTenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(ambientTenantId));
        var service = CreateLifecycleService(
            dbContext,
            currentTenant: new TestCurrentTenant(ambientTenantId));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetSelectionOptionsAsync(new GetTenantSelectionOptionsInput()));
    }

    [Fact]
    public void AllowedActions_should_be_fail_closed_and_status_aware()
    {
        var all = new Dictionary<string, bool>
        {
            ["Tenant.Management.Update"] = true,
            ["Tenant.Management.Lifecycle.Manage"] = true,
            ["Tenant.Management.ConnectionStrings.Update"] = true,
            ["Tenant.Management.Ownership.Transfer"] = true,
            ["Tenant.Management.Restore"] = true,
            ["Tenant.Management.Archive"] = true
        };

        // Live active tenant with an owner and all grants → the full non-restore set.
        var live = global::Tenant.Application.Tenants.TenantAllowedActionsCalculator.Compute(
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active, isDeleted: false, ownerUserId: Guid.NewGuid(), all);
        Assert.Equal(new[] { "rename", "lifecycle", "configureConnection", "transferOwnership", "archive" }, live);

        // No grants → nothing (fail closed).
        Assert.Empty(global::Tenant.Application.Tenants.TenantAllowedActionsCalculator.Compute(
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active, false, Guid.NewGuid(), new Dictionary<string, bool>()));

        // Soft-deleted → only restore.
        Assert.Equal(new[] { "restore" }, global::Tenant.Application.Tenants.TenantAllowedActionsCalculator.Compute(
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended, isDeleted: true, ownerUserId: Guid.NewGuid(), all));

        // Decommissioned (live) → no rename/lifecycle; archive stays coarse-allowed.
        var decommissioned = global::Tenant.Application.Tenants.TenantAllowedActionsCalculator.Compute(
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Decommissioned, false, Guid.NewGuid(), all);
        Assert.DoesNotContain("rename", decommissioned);
        Assert.DoesNotContain("lifecycle", decommissioned);
        Assert.Contains("archive", decommissioned);

        // No owner → no ownership transfer even when granted.
        Assert.DoesNotContain("transferOwnership", global::Tenant.Application.Tenants.TenantAllowedActionsCalculator.Compute(
            global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active, false, ownerUserId: null, all));
    }

    [Fact]
    public async Task GetListAsync_should_filter_by_lifecycle_active_connection_and_owner()
    {
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var ownerId = Guid.NewGuid();

        var activeConfigured = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "alpha");
        activeConfigured.SetOwner(ownerId, "Alice Admin", "alice@example.test");
        activeConfigured.SetConnectionString(Shared.Data.ConnectionStrings.TenantWrightDatabaseCatalog.AuditLogs, "Host=db;");
        var suspended = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "bravo");
        suspended.Deactivate();
        dbContext.Tenants.Add(activeConfigured);
        dbContext.Tenants.Add(suspended);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var suspendedOnly = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput
        {
            LifecycleStatuses = new() { global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended }
        });
        Assert.Equal("bravo", Assert.Single(suspendedOnly.Items).Name);

        var activeOnly = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput { IsActive = true });
        Assert.Equal("alpha", Assert.Single(activeOnly.Items).Name);

        var configuredOnly = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput { HasDedicatedDatabase = true });
        var configuredRow = Assert.Single(configuredOnly.Items);
        Assert.Equal("alpha", configuredRow.Name);
        Assert.True(configuredRow.HasDedicatedDatabase);

        var byOwner = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput { OwnerUserId = ownerId });
        var ownerRow = Assert.Single(byOwner.Items);
        Assert.Equal(ownerId, ownerRow.Owner!.UserId);
        Assert.Equal("Alice Admin", ownerRow.Owner!.DisplayName);
        Assert.Equal("alice@example.test", ownerRow.Owner!.Email);
    }

    [Fact]
    public async Task GetListAsync_should_sort_by_name_descending()
    {
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "alpha"));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "charlie"));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "bravo"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        var desc = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput
        {
            Sorting = "Name desc"
        });
        Assert.Equal(new[] { "charlie", "bravo", "alpha" }, desc.Items.Select(x => x.Name).ToArray());

        // A bare field name means ascending, and an undeclared field is refused rather than quietly
        // falling back to the default order.
        var ascending = await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput
        {
            Sorting = "Name"
        });
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, ascending.Items.Select(x => x.Name).ToArray());

        await Assert.ThrowsAsync<SharedValidationException>(
            () => service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput
            {
                Sorting = "ConnectionString"
            }));
    }

    [Fact]
    public async Task GetListAsync_should_expose_server_allowed_actions()
    {
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "alpha");
        tenant.SetOwner(Guid.NewGuid(), "Alice", "alice@example.test");
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext); // AllowAllPermissionChecker

        var row = Assert.Single((await service.GetListAsync(new Tenant.Application.Contracts.Tenants.GetTenantsInput())).Items);
        Assert.Contains("rename", row.AllowedActions);
        Assert.Contains("archive", row.AllowedActions);
        Assert.Contains("transferOwnership", row.AllowedActions);
        Assert.DoesNotContain("restore", row.AllowedActions);
    }

    [Fact]
    public async Task GetAsync_and_UpdateAsync_should_treat_soft_deleted_tenant_as_not_found()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "gone"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);
        await AddClosedClosureRequestAsync(dbContext, tenantId);
        await service.DeleteAsync(tenantId);

        await Assert.ThrowsAsync<Shared.Domain.Entities.EntityNotFoundException>(() => service.GetAsync(tenantId));
        await Assert.ThrowsAsync<Shared.Domain.Entities.EntityNotFoundException>(
            () => service.UpdateAsync(tenantId, new Tenant.Application.Contracts.Tenants.TenantUpdateDto { Name = "gone", IsActive = true }));
    }

    [Fact]
    public async Task CreateAsync_should_allow_reusing_a_soft_deleted_name_but_block_active_collisions()
    {
        var firstId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(firstId, "reuse"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var service = CreateLifecycleService(dbContext);

        await AddClosedClosureRequestAsync(dbContext, firstId);
        await service.DeleteAsync(firstId);
        var recreated = await service.CreateAsync(new Tenant.Application.Contracts.Tenants.TenantCreateDto { Name = "reuse" });
        Assert.Equal("reuse", recreated.Name);

        // A second live tenant with the same name is a real conflict.
        await Assert.ThrowsAsync<TenantNameAlreadyExistsException>(
            () => service.CreateAsync(new Tenant.Application.Contracts.Tenants.TenantCreateDto { Name = "reuse" }));
    }

    [Fact]
    public void TenantNameAlreadyExistsException_should_map_to_conflict()
    {
        // Ensures the global exception middleware translates name collisions to 409.
        Assert.IsAssignableFrom<Shared.SharedConflictException>(new TenantNameAlreadyExistsException("x"));
    }

    [Fact]
    public void InternalTenantsController_should_expose_provisioning_only_cleanup_route()
    {
        var action = typeof(InternalTenantsController).GetMethod(
            nameof(InternalTenantsController.DeleteProvisioningAsync));

        Assert.NotNull(action);
        var delete = Assert.Single(action!.GetCustomAttributes(typeof(HttpDeleteAttribute), inherit: true)
            .Cast<HttpDeleteAttribute>());
        Assert.Equal("{id:guid}/provisioning", delete.Template);
        Assert.Equal(
            "TenantInternalProvisioning",
            Assert.Single(action.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()).Policy);
    }

    [Fact]
    public void Tenant_serviceability_route_should_not_inherit_the_broader_internal_policy()
    {
        Assert.Empty(typeof(InternalTenantsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        var serviceability = typeof(InternalTenantsController).GetMethod(
            nameof(InternalTenantsController.GetServiceabilityBatchAsync));
        Assert.NotNull(serviceability);
        Assert.Equal(
            "TenantServiceabilityInternal",
            Assert.Single(serviceability!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()).Policy);

        var genericRead = typeof(InternalTenantsController).GetMethod(
            nameof(InternalTenantsController.FindByIdAsync));
        Assert.NotNull(genericRead);
        Assert.Equal(
            "TenantInternal",
            Assert.Single(genericRead!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()).Policy);
    }

    [Fact]
    public void TenantDeactivatedEto_should_have_stable_wire_name()
    {
        var attribute = Assert.Single(typeof(TenantDeactivatedEto).GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
            .Cast<EventNameAttribute>());

        Assert.Equal("Shared.EventContracts.TenantDeactivatedEto", attribute.Name);
    }

    [Fact]
    public async Task Connection_string_backfill_should_protect_legacy_rows_under_global_lock()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "legacy-org"));
        dbContext.TenantConnectionStrings.Add(
            new global::Tenant.Domain.Tenants.TenantConnectionString(
                tenantId,
                "Notifications",
                "Host=legacy-db;Password=plaintext"));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var protectionOptions = CreateProtectionOptions();
        var protector = new TenantConnectionStringProtector(
            new StaticOptionsMonitor<TenantConnectionStringProtectionOptions>(protectionOptions));
        var distributedLock = new RecordingDistributedLock();
        var service = new TenantConnectionStringBackfillService(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            protector,
            Options.Create(protectionOptions),
            distributedLock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantConnectionStringBackfillService>.Instance);

        Assert.Equal(1, await service.BackfillAsync());

        var stored = await dbContext.TenantConnectionStrings.SingleAsync();
        Assert.Null(stored.Value);
        Assert.NotNull(stored.ProtectedValue);
        Assert.DoesNotContain("plaintext", stored.ProtectedValue!, StringComparison.Ordinal);
        Assert.Equal("key-1", stored.ProtectionKeyId);

        var acquisition = Assert.Single(distributedLock.Acquisitions);
        Assert.Equal("tenant:connection-strings:protection-backfill", acquisition.Name);
        Assert.True(acquisition.Options.IgnoreTenant);
        Assert.Equal(DistributedLockFailureMode.FailClosed, acquisition.Options.FailureMode);
    }

    [Fact]
    public async Task Rewrap_should_migrate_an_old_key_row_to_the_current_key_and_refresh_the_cache()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "rotate-org"));
        await dbContext.SaveChangesOnDbContextAsync(true);

        // Two keys configured; the row is protected under the OLD key while
        // CurrentKeyId already points at the NEW key, simulating the state right
        // after step 1 of key rotation (new key added, CurrentKeyId flipped, old
        // key retained).
        var protectionOptions = CreateProtectionOptions();
        protectionOptions.Keys["key-2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var protector = new TenantConnectionStringProtector(
            new StaticOptionsMonitor<TenantConnectionStringProtectionOptions>(protectionOptions));

        const string plaintext = "Host=tenant-db;Database=rotate;Username=app;Password=s3cret";
        var oldKeyCiphertext = protector.Protect(tenantId, "Notifications", plaintext); // still protects under key-1 (CurrentKeyId at this point)
        dbContext.TenantConnectionStrings.Add(
            new global::Tenant.Domain.Tenants.TenantConnectionString(tenantId, "Notifications", oldKeyCiphertext, "key-1", 1, Guid.NewGuid().ToString("N")));
        await dbContext.SaveChangesOnDbContextAsync(true);

        // Flip CurrentKeyId to the new key, as an operator would for step 1 of rotation.
        protectionOptions.CurrentKeyId = "key-2";

        var byIdCache = new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string>();
        var cacheManager = CreateTenantCacheManager(dbContext, protector, byIdCache);
        var distributedLock = new RecordingDistributedLock();
        var service = new TenantConnectionStringRewrapService(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            protector,
            Options.Create(protectionOptions),
            distributedLock,
            cacheManager,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantConnectionStringRewrapService>.Instance);

        Assert.Equal(1, await service.RewrapAsync());

        var stored = await dbContext.TenantConnectionStrings.SingleAsync();
        Assert.Equal("key-2", stored.ProtectionKeyId);
        Assert.NotEqual(oldKeyCiphertext, stored.ProtectedValue);

        var acquisition = Assert.Single(distributedLock.Acquisitions);
        Assert.Equal("tenant:connection-strings:key-rewrap", acquisition.Name);

        // Cache refresh actually happened: the cached ciphertext is decryptable
        // even after the old key is removed entirely, proving it was re-wrapped
        // under key-2 in the refreshed cache, not left as stale key-1 ciphertext.
        protectionOptions.Keys.Remove("key-1");
        var cachedTenant = byIdCache.Get(tenantId.ToString("D"));
        Assert.NotNull(cachedTenant);
        var cachedCiphertext = cachedTenant!.ConnectionStrings["Notifications"];
        Assert.Equal(plaintext, protector.Unprotect(tenantId, "Notifications", cachedCiphertext));
    }

    [Fact]
    public async Task Rewrap_should_be_idempotent_when_all_rows_are_already_on_the_current_key()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "already-current"));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var protectionOptions = CreateProtectionOptions();
        var protector = new TenantConnectionStringProtector(
            new StaticOptionsMonitor<TenantConnectionStringProtectionOptions>(protectionOptions));
        var ciphertext = protector.Protect(tenantId, "Notifications", "Host=current-db");
        dbContext.TenantConnectionStrings.Add(
            new global::Tenant.Domain.Tenants.TenantConnectionString(tenantId, "Notifications", ciphertext, "key-1", 1, Guid.NewGuid().ToString("N")));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var cacheManager = CreateTenantCacheManager(dbContext, protector);
        var service = new TenantConnectionStringRewrapService(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            protector,
            Options.Create(protectionOptions),
            new RecordingDistributedLock(),
            cacheManager,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantConnectionStringRewrapService>.Instance);

        Assert.Equal(0, await service.RewrapAsync());

        var stored = await dbContext.TenantConnectionStrings.SingleAsync();
        Assert.Equal(ciphertext, stored.ProtectedValue); // untouched: no rewrap occurred
    }

    [Fact]
    public async Task Rewrap_should_throw_clearly_when_the_old_key_is_missing()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "orphaned-key"));
        await dbContext.SaveChangesOnDbContextAsync(true);

        // Protect a real envelope under "key-1" while it still exists in Keys...
        var protectionOptions = CreateProtectionOptions();
        var protector = new TenantConnectionStringProtector(
            new StaticOptionsMonitor<TenantConnectionStringProtectionOptions>(protectionOptions));
        var ciphertextUnderKey1 = protector.Protect(tenantId, "Notifications", "Host=orphaned-db");
        dbContext.TenantConnectionStrings.Add(
            new global::Tenant.Domain.Tenants.TenantConnectionString(tenantId, "Notifications", ciphertextUnderKey1, "key-1", 1, Guid.NewGuid().ToString("N")));
        await dbContext.SaveChangesOnDbContextAsync(true);

        // ...then simulate the old key being removed too early (a violation of
        // the two-deploy rotation process) and CurrentKeyId moved to a new key
        // that was never used to protect this row.
        protectionOptions.Keys.Remove("key-1");
        protectionOptions.Keys["key-2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        protectionOptions.CurrentKeyId = "key-2";

        var cacheManager = CreateTenantCacheManager(dbContext, protector);
        var service = new TenantConnectionStringRewrapService(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            protector,
            Options.Create(protectionOptions),
            new RecordingDistributedLock(),
            cacheManager,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantConnectionStringRewrapService>.Instance);

        // Must fail loudly (the row can't be rewrapped without its original
        // key), not silently skip the row and report success.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RewrapAsync());
    }

    [Fact]
    public async Task RefreshAllAsync_should_reassert_a_tombstone_for_soft_deleted_tenants()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "reconcile-tombstone"));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var byIdCache = new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string>();
        var cacheManager = CreateTenantCacheManager(dbContext, tenantByIdCache: byIdCache);

        var service = new TenantAppService(
            CreateTenantRepository(dbContext),
            tenantManager: new TenantManager(CreateTenantRepository(dbContext)),
            tenantCacheManager: cacheManager,
            currentTenant: currentTenant,
            eventBus: new NoopMessageSender(),
            unitOfWorkManager: new NoopUnitOfWorkManager(),
            dbContextProvider: new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dataFilter: dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            permissionChecker: new AllowAllPermissionChecker(),
            currentPrincipalAccessor: new TestCurrentPrincipalAccessor());

        await AddClosedClosureRequestAsync(dbContext, tenantId);
        await service.DeleteAsync(tenantId);

        // Simulate the tombstone's cache TTL expiring between delete and the
        // next reconcile pass (TombstoneTenantAsync only wrote it once, at
        // delete time).
        await byIdCache.RemoveAsync(tenantId.ToString("D"));
        Assert.Null(byIdCache.Get(tenantId.ToString("D")));

        // A reconcile pass must re-assert the tombstone from the database, not
        // just the live tenants, or the deleted tenant would revert to a
        // cold-miss and re-open the fail-open gap on host-fallback services.
        await cacheManager.RefreshAllAsync();

        var reasserted = byIdCache.Get(tenantId.ToString("D"));
        Assert.NotNull(reasserted);
        Assert.True(reasserted!.Exists);
        Assert.False(reasserted.IsActive);
    }

    [Fact]
    public void ReconcileWorker_should_use_shared_periodic_worker_contract()
    {
        Assert.True(typeof(IPeriodicBackgroundWorker).IsAssignableFrom(typeof(Tenant.Workers.TenantCacheReconcileWorker)));
        Assert.False(typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(typeof(Tenant.Workers.TenantCacheReconcileWorker)));
    }

    [Fact]
    public void ReconcileWorkerOptions_should_map_tenant_cache_interval_to_shared_worker_options()
    {
        var workerOptions = Tenant.Workers.TenantCacheReconcileWorkerOptions.FromTenantCache(
            new TenantCacheOptions
            {
                ReconcileEnabled = true,
                ReconcileIntervalSeconds = 300
            });

        Assert.True(workerOptions.Enabled);
        Assert.Equal(Tenant.Workers.TenantCacheReconcileWorker.WorkerName, workerOptions.WorkerName);
        Assert.Equal(Tenant.Workers.TenantCacheReconcileWorker.OwnershipLockName, workerOptions.DistributedLockName);
        Assert.Equal(300_000, workerOptions.PollIntervalMs);
        Assert.Equal(300_000, workerOptions.OwnershipLeaseMs);
    }

    [Fact]
    public async Task Tenant_list_cache_mutation_should_use_global_fail_closed_lock()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(currentTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "lock-org"));
        await dbContext.SaveChangesOnDbContextAsync(true);
        var distributedLock = new RecordingDistributedLock();
        var cacheManager = CreateTenantCacheManager(dbContext, distributedLock: distributedLock);

        await cacheManager.UpsertTenantAsync(tenantId);

        var acquisition = Assert.Single(distributedLock.Acquisitions);
        Assert.Equal("tenant:list-cache:mutation", acquisition.Name);
        Assert.Equal(Shared.DistributedLocking.Policies.DistributedLockPolicyNames.TenantListCacheMutation, acquisition.Options.PolicyName);
        Assert.True(acquisition.Options.IgnoreTenant);
        Assert.True(acquisition.HandleDisposed);
    }

    [Fact]
    public void Tenant_app_service_should_participate_in_ambient_unit_of_work()
    {
        Assert.True(typeof(Shared.Uow.Abstractions.IUnitOfWorkEnabled).IsAssignableFrom(typeof(TenantAppService)));
        Assert.Contains(
            typeof(TenantAppService).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(Shared.Uow.Abstractions.IUnitOfWorkManager));
    }

    [Fact]
    public void Provisioning_request_should_track_retry_and_identity_acknowledgement()
    {
        var now = DateTime.UtcNow;
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            previousOwnerUserId: null,
            targetEmail: "owner@example.test",
            suggestedUserName: null,
            reasonCode: "public-registration",
            note: null,
            TenantOwnershipRequestKind.InitialOwner,
            registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: Guid.NewGuid());

        request.RecordProvisioningRetry(now, "support-retry", "Retry before acknowledgement");
        Assert.Equal(1, request.ProvisioningRetryCount);
        Assert.Equal(now, request.LastProvisioningRetryAtUtc);

        var invitationId = Guid.NewGuid();
        Assert.True(request.AcknowledgeIdentityInvitation(invitationId, now.AddSeconds(1)));
        Assert.Equal(TenantOwnershipRequestStatus.AwaitingEmailVerification, request.Status);
        Assert.Equal(invitationId, request.IdentityInvitationId);
        Assert.Equal(now.AddSeconds(1), request.InvitationCreatedAtUtc);
        Assert.False(request.AcknowledgeIdentityInvitation(invitationId, now.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            request.RecordProvisioningRetry(now.AddSeconds(3), "late-retry", null));
    }

    [Fact]
    public void Provisioning_contracts_and_routes_should_be_safe_and_exactly_authorized()
    {
        var dtoProperties = typeof(TenantProvisioningOperationDto).GetProperties()
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(dtoProperties, x => x.Contains("Email", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dtoProperties, x => x.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dtoProperties, x => x.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase));

        var hostRetry = typeof(TenantsController).GetMethod(nameof(TenantsController.RetryProvisioningOperationAsync));
        Assert.NotNull(hostRetry);
        var hostPolicy = Assert.Single(hostRetry!.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal(TenantPermissions.Provisioning.Retry, hostPolicy.Policy);

        Assert.NotNull(typeof(TenantsController).GetMethod(nameof(TenantsController.GetProvisioningOperationAsync)));
        Assert.NotNull(typeof(InternalTenantsController).GetMethod(nameof(InternalTenantsController.GetPublicRegistrationAsync)));
        Assert.NotNull(typeof(InternalTenantsController).GetMethod(nameof(InternalTenantsController.RetryPublicRegistrationAsync)));

        var eventName = Assert.Single(typeof(TenantOwnershipInvitationCreatedEto)
            .GetCustomAttributes(typeof(EventNameAttribute), false)
            .Cast<EventNameAttribute>());
        Assert.Equal("Shared.EventContracts.TenantOwnershipInvitationCreatedEto", eventName.Name);
    }

    [Fact]
    public void Provisioning_retry_should_be_bounded_and_reject_expired_request()
    {
        var now = DateTime.UtcNow;
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(), Guid.NewGuid(), null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            expiresAtUtc: now.AddHours(1), registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: Guid.NewGuid());

        for (var attempt = 0; attempt < TenantOwnershipRequest.MaxProvisioningRetryCount; attempt++)
            request.RecordProvisioningRetry(now.AddSeconds(attempt * 31), "bounded-retry", null);

        Assert.Throws<InvalidOperationException>(() =>
            request.RecordProvisioningRetry(now.AddMinutes(10), "over-limit", null));

        var expired = new TenantOwnershipRequest(
            Guid.NewGuid(), Guid.NewGuid(), null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            expiresAtUtc: now.AddSeconds(-1), registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() =>
            expired.RecordProvisioningRetry(now, "expired-retry", null));
        Assert.Equal(TenantOwnershipRequestStatus.Expired, expired.Status);
    }

    [Fact]
    public async Task Provisioning_retry_should_republish_same_request_through_outbox_and_enforce_cooldown()
    {
        var tenantId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "retry-org");
        tenant.BeginProvisioning();
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(), tenantId, null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: operationId);
        dbContext.Tenants.Add(tenant);
        dbContext.TenantOwnershipRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var eventBus = new RecordingMessageSender();
        var service = CreateLifecycleService(dbContext, eventBus);

        var result = await service.RetryProvisioningOperationAsync(tenantId, new TenantProvisioningRetryInput
        {
            ReasonCode = "support-retry",
            Note = "No creation acknowledgement was observed."
        });

        Assert.Equal(request.Id, result.OwnershipRequestId);
        Assert.Equal(1, result.ProvisioningRetryCount);
        Assert.False(result.CanRetry);
        var published = Assert.Single(eventBus.Published.OfType<TenantOwnershipInvitationRequestedEto>());
        Assert.Equal(request.Id, published.RequestId);
        Assert.Equal(tenantId, published.TenantId);
        Assert.Equal("owner@example.test", published.TargetEmail);
        var publishRecord = Assert.Single(eventBus.PublishRecords.Where(x => x.EventData is TenantOwnershipInvitationRequestedEto));
        Assert.True(publishRecord.OnUnitOfWorkComplete);
        Assert.True(publishRecord.UseOutbox);

        await dbContext.SaveChangesOnDbContextAsync(true);
        var status = await service.GetPublicRegistrationAsync(operationId);
        Assert.NotNull(status);
        Assert.Equal(1, status!.ProvisioningRetryCount);
        await Assert.ThrowsAsync<SharedValidationException>(() =>
            service.RetryPublicRegistrationAsync(operationId));
    }

    [Fact]
    public async Task Identity_creation_ack_should_be_idempotent_and_block_further_tenant_retry()
    {
        var tenantId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var occurredAtUtc = DateTime.UtcNow;
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "ack-org");
        tenant.BeginProvisioning();
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(), tenantId, null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: operationId);
        dbContext.Tenants.Add(tenant);
        dbContext.TenantOwnershipRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var handler = new TenantOwnershipInvitationCreatedEventHandler(new StaticDbContextProvider<TenantDbContextApp>(dbContext));
        var acknowledgement = new TenantOwnershipInvitationCreatedEto
        {
            RequestId = request.Id,
            TenantId = tenantId,
            InvitationId = invitationId,
            OccurredAtUtc = occurredAtUtc
        };

        await handler.HandleAsync(acknowledgement);
        await handler.HandleAsync(acknowledgement);

        var stored = await dbContext.TenantOwnershipRequests.SingleAsync(x => x.Id == request.Id);
        Assert.Equal(TenantOwnershipRequestStatus.AwaitingEmailVerification, stored.Status);
        Assert.Equal(invitationId, stored.IdentityInvitationId);
        Assert.Equal(occurredAtUtc, stored.InvitationCreatedAtUtc);
        var service = CreateLifecycleService(dbContext);
        var status = await service.GetProvisioningOperationAsync(tenantId);
        Assert.False(status.CanRetry);
        Assert.False(status.IsTerminal);
        await Assert.ThrowsAsync<SharedValidationException>(() =>
            service.RetryProvisioningOperationAsync(tenantId, new TenantProvisioningRetryInput
            {
                ReasonCode = "late-retry"
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new TenantOwnershipInvitationCreatedEto
        {
            RequestId = request.Id,
            TenantId = tenantId,
            InvitationId = Guid.NewGuid(),
            OccurredAtUtc = occurredAtUtc.AddMinutes(1)
        }));
    }

    [Fact]
    public async Task Identity_creation_ack_with_wrong_tenant_should_not_mutate_request()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(), tenantId, null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: Guid.NewGuid());
        dbContext.TenantOwnershipRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var handler = new TenantOwnershipInvitationCreatedEventHandler(new StaticDbContextProvider<TenantDbContextApp>(dbContext));

        await handler.HandleAsync(new TenantOwnershipInvitationCreatedEto
        {
            RequestId = request.Id,
            TenantId = Guid.NewGuid(),
            InvitationId = Guid.NewGuid(),
            OccurredAtUtc = DateTime.UtcNow
        });

        Assert.Equal(TenantOwnershipRequestStatus.PendingInvitation, request.Status);
        Assert.Null(request.IdentityInvitationId);
    }

    [Fact]
    public async Task Identity_verification_should_complete_owner_but_wait_for_permissions_before_activation()
    {
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "permissions-wait-tenantwright");
        tenant.BeginProvisioning();
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(), tenantId, null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: Guid.NewGuid());
        request.AcknowledgeIdentityInvitation(invitationId, DateTime.UtcNow);
        dbContext.Tenants.Add(tenant);
        dbContext.TenantOwnershipRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var eventBus = new RecordingMessageSender();
        var verifiedUnitOfWork = new TestUnitOfWork();
        var verifiedHandler = new TenantOwnershipInvitationVerifiedEventHandler(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            eventBus,
            CreateTenantCacheManager(dbContext),
            new NoopUnitOfWorkManager(verifiedUnitOfWork),
            NullLogger<TenantOwnershipInvitationVerifiedEventHandler>.Instance);

        await verifiedHandler.HandleAsync(new TenantOwnershipInvitationVerifiedEto
        {
            RequestId = request.Id,
            TenantId = tenantId,
            UserId = ownerUserId,
            OccurredAtUtc = DateTime.UtcNow
        });
        await verifiedUnitOfWork.CompleteAsync();

        Assert.Equal(ownerUserId, tenant.OwnerUserId);
        Assert.Equal(TenantOwnershipRequestStatus.Completed, request.Status);
        Assert.Equal(TenantLifecycleStatus.Provisioning, tenant.GetEffectiveLifecycleStatus());
        Assert.False(tenant.IsActive);
        var completed = Assert.Single(eventBus.Published.OfType<TenantOwnershipCompletedEto>());
        Assert.Equal(request.Id, completed.RequestId);

        var permissionsUnitOfWork = new TestUnitOfWork();
        var permissionsHandler = new TenantAdminGrantsProvisionedEventHandler(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            eventBus,
            CreateTenantCacheManager(dbContext),
            new NoopUnitOfWorkManager(permissionsUnitOfWork),
            NullLogger<TenantAdminGrantsProvisionedEventHandler>.Instance);
        var permissionsReadyAtUtc = DateTime.UtcNow.AddSeconds(1);
        var acknowledgement = new TenantAdminGrantsProvisionedEto
        {
            RequestId = request.Id,
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            OccurredAtUtc = permissionsReadyAtUtc
        };
        await permissionsHandler.HandleAsync(acknowledgement);
        await permissionsHandler.HandleAsync(acknowledgement);
        await permissionsUnitOfWork.CompleteAsync();

        Assert.Equal(TenantLifecycleStatus.Active, tenant.GetEffectiveLifecycleStatus());
        Assert.True(tenant.IsActive);
        Assert.Equal(permissionsReadyAtUtc, request.PermissionsProvisionedAtUtc);
        var status = await CreateLifecycleService(dbContext).GetProvisioningOperationAsync(tenantId);
        Assert.True(status.IsReady);
        Assert.True(status.IsTerminal);
        Assert.Equal(permissionsReadyAtUtc, status.PermissionsProvisionedAtUtc);
    }

    [Fact]
    public async Task Ownership_completion_post_commit_callback_should_refresh_cache_without_requerying_after_commit()
    {
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "external-owner-cache-tenantwright");
        tenant.BeginProvisioning();
        tenant.SetConnectionString("Notifications", "Host=tenant-owner-cache");
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(), tenantId, null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: Guid.NewGuid());
        request.AcknowledgeIdentityInvitation(invitationId, DateTime.UtcNow);
        dbContext.Tenants.Add(tenant);
        dbContext.TenantOwnershipRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        dbContext.ChangeTracker.Clear();

        var tenantByIdCache = new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string>();
        var unitOfWork = new TestUnitOfWork();
        var handler = new TenantOwnershipInvitationVerifiedEventHandler(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            new RecordingMessageSender(),
            CreateTenantCacheManager(dbContext, tenantByIdCache: tenantByIdCache),
            new NoopUnitOfWorkManager(unitOfWork),
            NullLogger<TenantOwnershipInvitationVerifiedEventHandler>.Instance);

        await handler.HandleAsync(new TenantOwnershipInvitationVerifiedEto
        {
            RequestId = request.Id,
            TenantId = tenantId,
            UserId = ownerUserId,
            UserName = "owner",
            DisplayName = "Tenant Owner",
            Email = "owner@example.test",
            OccurredAtUtc = DateTime.UtcNow
        });

        Assert.Equal(1, unitOfWork.CompletionHandlerCount);
        await dbContext.DisposeAsync();
        await unitOfWork.CompleteAsync();

        var cachedTenant = tenantByIdCache.Get(tenantId.ToString("D"));
        Assert.NotNull(cachedTenant);
        Assert.Equal("Host=tenant-owner-cache", cachedTenant.ConnectionStrings["Notifications"]);
    }

    [Fact]
    public async Task Permissions_activation_post_commit_callback_should_refresh_cache_without_requerying_after_commit()
    {
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "external-activation-cache-tenantwright");
        tenant.BeginProvisioning();
        tenant.SetOwner(ownerUserId);
        tenant.SetConnectionString("Notifications", "Host=tenant-activation-cache");
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(), tenantId, null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: Guid.NewGuid());
        request.AcknowledgeIdentityInvitation(Guid.NewGuid(), DateTime.UtcNow);
        request.Complete(ownerUserId, DateTime.UtcNow);
        dbContext.Tenants.Add(tenant);
        dbContext.TenantOwnershipRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        dbContext.ChangeTracker.Clear();

        var tenantByIdCache = new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string>();
        var unitOfWork = new TestUnitOfWork();
        var handler = new TenantAdminGrantsProvisionedEventHandler(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            new RecordingMessageSender(),
            CreateTenantCacheManager(dbContext, tenantByIdCache: tenantByIdCache),
            new NoopUnitOfWorkManager(unitOfWork),
            NullLogger<TenantAdminGrantsProvisionedEventHandler>.Instance);

        await handler.HandleAsync(new TenantAdminGrantsProvisionedEto
        {
            RequestId = request.Id,
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            OccurredAtUtc = DateTime.UtcNow
        });

        Assert.Equal(1, unitOfWork.CompletionHandlerCount);
        await dbContext.DisposeAsync();
        await unitOfWork.CompleteAsync();

        var cachedTenant = tenantByIdCache.Get(tenantId.ToString("D"));
        Assert.NotNull(cachedTenant);
        Assert.True(cachedTenant.IsActive);
        Assert.Equal("Host=tenant-activation-cache", cachedTenant.ConnectionStrings["Notifications"]);
    }

    [Fact]
    public async Task Permissions_readiness_with_wrong_owner_should_not_activate_tenant()
    {
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(new TestCurrentTenant(null));
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "permissions-mismatch-tenantwright");
        tenant.BeginProvisioning();
        tenant.SetOwner(ownerUserId);
        var request = new TenantOwnershipRequest(
            Guid.NewGuid(), tenantId, null, "owner@example.test", null,
            "public-registration", null, TenantOwnershipRequestKind.InitialOwner,
            registrationOrigin: TenantRegistrationOrigin.PublicAuth,
            registrationOperationId: Guid.NewGuid());
        request.AcknowledgeIdentityInvitation(Guid.NewGuid(), DateTime.UtcNow);
        request.Complete(ownerUserId, DateTime.UtcNow);
        dbContext.Tenants.Add(tenant);
        dbContext.TenantOwnershipRequests.Add(request);
        await dbContext.SaveChangesOnDbContextAsync(true);
        var handler = new TenantAdminGrantsProvisionedEventHandler(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            new RecordingMessageSender(),
            CreateTenantCacheManager(dbContext),
            new NoopUnitOfWorkManager(new TestUnitOfWork()),
            NullLogger<TenantAdminGrantsProvisionedEventHandler>.Instance);

        await handler.HandleAsync(new TenantAdminGrantsProvisionedEto
        {
            RequestId = request.Id,
            TenantId = tenantId,
            OwnerUserId = Guid.NewGuid(),
            OccurredAtUtc = DateTime.UtcNow
        });

        Assert.Equal(TenantLifecycleStatus.Provisioning, tenant.GetEffectiveLifecycleStatus());
        Assert.Null(request.PermissionsProvisionedAtUtc);
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            Id = id;
            return new DisposeAction(() => Id = previousId);
        }
    }

    private sealed class TestCurrentUser(Guid userId, Guid tenantId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? Id => userId;
        public string? UserName => "owner";
        public string? Name => "Owner";
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => "owner@example.test";
        public bool EmailVerified => true;
        public Guid? TenantId => tenantId;
        public string? SessionId => null;
        public string[] Roles => [];
        public System.Security.Claims.Claim? FindClaim(string claimType) => null;
        public System.Security.Claims.Claim[] FindClaims(string claimType) => [];
        public System.Security.Claims.Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }

    internal static Shared.Domain.Repositories.IRepository<global::Tenant.Domain.Tenants.Tenant, Guid> CreateTenantRepository(TenantDbContextApp dbContext)
    {
        return new EfCoreRepository<TenantDbContextApp, global::Tenant.Domain.Tenants.Tenant, Guid>(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dbContext.ServiceProvider ?? throw new InvalidOperationException("Test DbContext has no service provider."));
    }

    internal static Shared.Domain.Repositories.IRepository<global::Tenant.Domain.Tenants.TenantOwnershipRequest, Guid> CreateOwnershipRepository(TenantDbContextApp dbContext)
    {
        return new EfCoreRepository<TenantDbContextApp, global::Tenant.Domain.Tenants.TenantOwnershipRequest, Guid>(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dbContext.ServiceProvider ?? throw new InvalidOperationException("Test DbContext has no service provider."));
    }

    internal static Shared.Domain.Repositories.IRepository<global::Tenant.Domain.Tenants.TenantLifecycleTransition, Guid> CreateTransitionRepository(TenantDbContextApp dbContext)
    {
        return new EfCoreRepository<TenantDbContextApp, global::Tenant.Domain.Tenants.TenantLifecycleTransition, Guid>(
            new StaticDbContextProvider<TenantDbContextApp>(dbContext),
            dbContext.ServiceProvider ?? throw new InvalidOperationException("Test DbContext has no service provider."));
    }

    private static TenantDbContextApp CreateDbContext(TestCurrentTenant currentTenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenant>(currentTenant);
        services.AddSingleton<IDataFilter, TestDataFilter>();
        services.AddSingleton<IAuditPropertySetter, NoopAuditPropertySetter>();
        services.AddSingleton<IAuditingManager, NoopAuditingManager>();
        services.AddSingleton<Shared.Uow.Abstractions.IUnitOfWorkManager, NoopUnitOfWorkManager>();
        services.AddSingleton<ITenantWrightMessageSender, NoopMessageSender>();
        services.AddSingleton<ICancellationTokenProvider, TestCancellationTokenProvider>();
        services.AddSingleton<IClock, UtcClock>();

        var provider = services.BuildServiceProvider();
        var options = new DbContextOptionsBuilder<TenantDbContextApp>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        var dbContext = new TenantDbContextApp(options)
        {
            ServiceProvider = provider
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new TestUnitOfWork()));
        return dbContext;
    }

    private sealed class StaticDbContextProvider<TDbContext> : IDbContextProvider<TDbContext>
        where TDbContext : class, IEfCoreDbContext
    {
        private readonly TDbContext _dbContext;

        public StaticDbContextProvider(TDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public Task<TDbContext> GetDbContextAsync()
        {
            return Task.FromResult(_dbContext);
        }
    }

    private sealed class TestCancellationTokenProvider : ICancellationTokenProvider
    {
        public CancellationToken Token => CancellationToken.None;
    }

    private static TenantCacheManager CreateTenantCacheManager(
        TenantDbContextApp dbContext,
        ITenantConnectionStringProtector? protector = null,
        InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string>? tenantByIdCache = null,
        IDistributedLock? distributedLock = null)
    {
        protector ??= new NoopTenantConnectionStringProtector();
        return new TenantCacheManager(
            CreateTenantRepository(dbContext),
            tenantByIdCache ?? new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string>(),
            new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantByNameCacheItem, string>(),
            new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantListCacheItem, string>(),
            new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantListStampCacheItem, string>(),
            protector,
            distributedLock ?? new RecordingDistributedLock(),
            dbContext.ServiceProvider.GetRequiredService<IDataFilter>(),
            Options.Create(new TenantCacheOptions()),
            NullLogger<TenantCacheManager>.Instance);
    }

    private static TenantConnectionStringProtectionOptions CreateProtectionOptions()
    {
        return new TenantConnectionStringProtectionOptions
        {
            CurrentKeyId = "key-1",
            CurrentKeyVersion = 1,
            Keys =
            {
                ["key-1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        };
    }

    private sealed class AllowAllPermissionChecker : Shared.Permissions.Authorization.IPermissionChecker
    {
        public Task<bool> IsGrantedAsync(System.Security.Claims.ClaimsPrincipal principal, string permissionName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<Dictionary<string, bool>> IsGrantedAsync(System.Security.Claims.ClaimsPrincipal principal, IReadOnlyList<string> permissionNames, CancellationToken cancellationToken = default)
            => Task.FromResult(permissionNames.ToDictionary(name => name, _ => true));
    }

    private sealed class TestCurrentPrincipalAccessor : Shared.Security.ICurrentPrincipalAccessor
    {
        public System.Security.Claims.ClaimsPrincipal Principal { get; } = new(new System.Security.Claims.ClaimsIdentity());
        public IDisposable Change(System.Security.Claims.ClaimsPrincipal principal) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class NoopTenantConnectionStringProtector : ITenantConnectionStringProtector
    {
        public string Protect(Guid tenantId, string connectionName, string plaintext) => plaintext;

        public string Unprotect(Guid tenantId, string connectionName, string storedValue) => storedValue;

        public bool IsProtected(string? storedValue) => false;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public StaticOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _dispose;

        public DisposeAction(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            _dispose();
        }
    }

    // Functional data filter so ISoftDelete can actually be toggled: the shared
    // DbContext query filter reads IsEnabled<ISoftDelete>() live, and Disable(...)
    // is what FindTenantAsync/GetListAsync use to see soft-deleted rows. Filters
    // default enabled (empty dict), matching production defaults.
    private sealed class TestDataFilter : IDataFilter
    {
        private readonly Dictionary<Type, int> _disableDepth = new();

        public IDisposable Enable<TFilter>() where TFilter : class => new DisposeAction(() => { });

        public IDisposable Disable<TFilter>() where TFilter : class
        {
            var key = typeof(TFilter);
            _disableDepth[key] = _disableDepth.GetValueOrDefault(key) + 1;
            return new DisposeAction(() => _disableDepth[key] = _disableDepth.GetValueOrDefault(key) - 1);
        }

        public bool IsEnabled<TFilter>() where TFilter : class => _disableDepth.GetValueOrDefault(typeof(TFilter)) <= 0;
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
        public IAuditLogSaveHandle BeginScope() => new NoopAuditLogScope();

        private sealed class NoopAuditLogScope : IAuditLogScope, IAuditLogSaveHandle
        {
            public AuditLogInfo Log { get; } = new();
            public Task SaveAsync() => Task.CompletedTask;
            public void Dispose() { }
        }
    }

    private sealed class NoopUnitOfWorkManager(
        Shared.Uow.Abstractions.IUnitOfWork? current = null)
        : Shared.Uow.Abstractions.IUnitOfWorkManager
    {
        public Shared.Uow.Abstractions.IUnitOfWork? Current => current;
        public Shared.Uow.Abstractions.IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false)
            => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Begin(bool requiresNew = false)
            => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Reserve(string reservationName, bool requiresNew = false)
            => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options)
            => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }

    private sealed class NoopMessageSender : ITenantWrightMessageSender
    {
        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
    }

    private sealed class TestUnitOfWork : Shared.Uow.Abstractions.IUnitOfWork
    {
        private readonly List<Func<Task>> _completionHandlers = [];

        public int CompletionHandlerCount => _completionHandlers.Count;
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
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
        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            IsCompleted = true;
            foreach (var handler in _completionHandlers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await handler();
            }
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void OnCompleted(Func<Task> handler) => _completionHandlers.Add(handler);
        public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => factory(key);
        public Shared.Uow.Abstractions.IDatabaseApi? FindDatabaseApi(string key) => null;
        public void AddDatabaseApi(string key, Shared.Uow.Abstractions.IDatabaseApi api) { }
        public Shared.Uow.Abstractions.IDatabaseApi GetOrAddDatabaseApi(string key, Func<Shared.Uow.Abstractions.IDatabaseApi> factory) => factory();
        public Shared.Uow.Abstractions.ITransactionApi? FindTransactionApi(string key) => null;
        public void AddTransactionApi(string key, Shared.Uow.Abstractions.ITransactionApi api) { }
        public Shared.Uow.Abstractions.ITransactionApi GetOrAddTransactionApi(string key, Func<Shared.Uow.Abstractions.ITransactionApi> factory) => factory();
        public void Dispose()
        {
            IsDisposed = true;
            Disposed?.Invoke(this, new Shared.Uow.Events.UnitOfWorkEventArgs(this));
        }
    }

    private sealed class RecordingMessageSender : ITenantWrightMessageSender
    {
        public List<object> Commands { get; } = [];
        public List<object> Published { get; } = [];
        public List<(object EventData, bool OnUnitOfWorkComplete, bool UseOutbox)> PublishRecords { get; } = [];

        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.CompletedTask;
        }
        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
        {
            Published.Add(message);
            PublishRecords.Add((message, true, true));
            return Task.CompletedTask;
        }
    }
    private sealed class InMemoryTypedCache<TCacheItem, TCacheKey> : IDistributedCache<TCacheItem, TCacheKey>
        where TCacheItem : class
        where TCacheKey : notnull
    {
        private readonly Dictionary<TCacheKey, TCacheItem> _items = new();

        public TCacheItem? Get(TCacheKey key, bool? hideErrors = null, bool considerUow = false) => _items.GetValueOrDefault(key);
        public KeyValuePair<TCacheKey, TCacheItem?>[] GetMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false)
            => keys.Select(x => new KeyValuePair<TCacheKey, TCacheItem?>(x, _items.GetValueOrDefault(x))).ToArray();
        public Task<KeyValuePair<TCacheKey, TCacheItem?>[]> GetManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
            => Task.FromResult(GetMany(keys, hideErrors, considerUow));
        public Task<TCacheItem?> GetAsync(TCacheKey key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
            => Task.FromResult(Get(key, hideErrors, considerUow));
        public TCacheItem? GetOrAdd(TCacheKey key, Func<TCacheItem> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
            => _items.TryGetValue(key, out var value) ? value : (_items[key] = factory());
        public async Task<TCacheItem?> GetOrAddAsync(TCacheKey key, Func<Task<TCacheItem>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            if (_items.TryGetValue(key, out var value))
            {
                return value;
            }

            value = await factory();
            _items[key] = value;
            return value;
        }
        public KeyValuePair<TCacheKey, TCacheItem?>[] GetOrAddMany(IEnumerable<TCacheKey> keys, Func<IEnumerable<TCacheKey>, List<KeyValuePair<TCacheKey, TCacheItem>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var pair in factory(keys.Where(x => !_items.ContainsKey(x))))
            {
                _items[pair.Key] = pair.Value;
            }

            return GetMany(keys, hideErrors, considerUow);
        }
        public async Task<KeyValuePair<TCacheKey, TCacheItem?>[]> GetOrAddManyAsync(IEnumerable<TCacheKey> keys, Func<IEnumerable<TCacheKey>, Task<List<KeyValuePair<TCacheKey, TCacheItem>>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            foreach (var pair in await factory(keys.Where(x => !_items.ContainsKey(x))))
            {
                _items[pair.Key] = pair.Value;
            }

            return GetMany(keys, hideErrors, considerUow);
        }
        public void Set(TCacheKey key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false) => _items[key] = value;
        public Task SetAsync(TCacheKey key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }
        public void SetMany(IEnumerable<KeyValuePair<TCacheKey, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var pair in items)
            {
                _items[pair.Key] = pair.Value;
            }
        }
        public Task SetManyAsync(IEnumerable<KeyValuePair<TCacheKey, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            SetMany(items, options, hideErrors, considerUow);
            return Task.CompletedTask;
        }
        public void Refresh(TCacheKey key, bool? hideErrors = null) { }
        public Task RefreshAsync(TCacheKey key, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
        public void RefreshMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null) { }
        public Task RefreshManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(TCacheKey key, bool? hideErrors = null, bool considerUow = false) => _items.Remove(key);
        public Task RemoveAsync(TCacheKey key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }
        public void RemoveMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var key in keys)
            {
                _items.Remove(key);
            }
        }
        public Task RemoveManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            RemoveMany(keys, hideErrors, considerUow);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDistributedLock : IDistributedLock
    {
        public List<LockAcquisition> Acquisitions { get; } = [];

        public Task<IDistributedLockHandle?> TryAcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
        {
            var acquisition = new LockAcquisition(name, options);
            Acquisitions.Add(acquisition);
            return Task.FromResult<IDistributedLockHandle?>(new RecordingLockHandle(acquisition));
        }

        public Task<IDistributedLockHandle> AcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
        {
            var acquisition = new LockAcquisition(name, options);
            Acquisitions.Add(acquisition);
            return Task.FromResult<IDistributedLockHandle>(new RecordingLockHandle(acquisition));
        }

        public sealed record LockAcquisition(string Name, DistributedLockAcquireOptions Options)
        {
            public bool HandleDisposed { get; set; }
        }

        private sealed class RecordingLockHandle : IDistributedLockHandle
        {
            private readonly LockAcquisition _acquisition;

            public RecordingLockHandle(LockAcquisition acquisition)
            {
                _acquisition = acquisition;
            }

            public string Name => _acquisition.Name;

            public ValueTask DisposeAsync()
            {
                _acquisition.HandleDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

}
