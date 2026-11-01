using Microsoft.Data.Sqlite;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Provisioning;
using Permissions.Application.PermissionManagement;
using Permissions.Application.Definitions;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Shared.EventContracts;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Controllers.InternalProvisioning;
using Permissions.Tests.TestDoubles;
using Shared.EventBus.Messaging;
using Shared.Events;
using Shared.Modularity.DependencyInjection;
using Shared.Permissions.Definitions.Tenant;
using Shared.Permissions.Definitions.AuditLogs;
using Xunit;

namespace Permissions.Tests;

public sealed class TenantAdminGrantProvisioningContractTests
{
    [Fact]
    public async Task Delete_endpoint_should_delegate_tenant_admin_grant_compensation()
    {
        var tenantId = Guid.NewGuid();
        var service = new RecordingTenantAdminGrantProvisioningService();
        var controller = new InternalProvisioningController(service);

        var result = await controller.DeleteTenantAdminGrantsAsync(tenantId, CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Equal(tenantId, service.DeletedTenantId);
        var action = typeof(InternalProvisioningController).GetMethod(
            nameof(InternalProvisioningController.DeleteTenantAdminGrantsAsync));
        var delete = Assert.Single(action!.GetCustomAttributes(typeof(HttpDeleteAttribute), inherit: true)
            .Cast<HttpDeleteAttribute>());
        Assert.Equal("tenants/{tenantId:guid}/roles/tenant-admin/grants", delete.Template);
    }

    [Fact]
    public async Task Ownership_completion_should_provision_grants_and_publish_readiness_ack_through_outbox()
    {
        var requestId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var service = new RecordingTenantAdminGrantProvisioningService();
        var messages = new RecordingTenantWrightMessageSender();
        var handler = new TenantOwnershipCompletedGrantProvisioningEventHandler(service, messages);

        await handler.HandleAsync(new TenantOwnershipCompletedEto
        {
            RequestId = requestId,
            TenantId = tenantId,
            NewOwnerUserId = ownerUserId,
            OccurredAtUtc = DateTime.UtcNow
        });

        Assert.Equal(tenantId, service.UpsertedTenantId);
        var acknowledgement = Assert.IsType<TenantAdminGrantsProvisionedEto>(Assert.Single(messages.Published));
        Assert.Equal(requestId, acknowledgement.RequestId);
        Assert.Equal(tenantId, acknowledgement.TenantId);
        Assert.Equal(ownerUserId, acknowledgement.OwnerUserId);

        var eventName = Assert.Single(typeof(TenantAdminGrantsProvisionedEto)
            .GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
            .Cast<EventNameAttribute>());
        Assert.Equal("Shared.EventContracts.TenantAdminGrantsProvisionedEto", eventName.Name);
    }

    [Fact]
    public async Task Tenant_admin_upsert_should_persist_before_publishing_non_empty_cache_projection()
    {
        var tenantId = Guid.NewGuid();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = PermissionsEfCoreTestHarness.BuildSqliteDbContext(
            connection, tenantId: null, multiTenantFilterEnabled: false);
        await dbContext.Database.EnsureCreatedAsync();
        var repository = new EfTestRepository<PermissionGrant, Guid>(dbContext);
        var cacheProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var grantsCache = cacheProvider.GetRequiredService<PermissionProviderGrantsCache>();
        var stampService = new PermissionStampService(
            cacheProvider.GetRequiredService<PermissionProviderStampCache>(),
            new RecordingTenantWrightMessageSender());
        var syncService = new PermissionProviderGrantCacheSyncService(
            repository,
            grantsCache,
            cacheProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var definitionManager = new PermissionDefinitionManager(
            new PermissionDefinitionProvider[] { new TenantPermissionDefinitionProvider() });
        var service = new TenantAdminGrantProvisioningService(
            definitionManager,
            repository,
            syncService,
            stampService,
            new TestCurrentTenant(null),
            PermissionsEfCoreTestHarness.AsDbContextProvider(dbContext));

        await service.UpsertTenantAdminGrantsAsync(tenantId);
        await service.UpsertTenantAdminGrantsAsync(tenantId);

        var grants = await dbContext.PermissionGrants.AsNoTracking()
            .Where(x => x.TenantId == tenantId &&
                        x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D"))
            .ToListAsync();
        Assert.NotEmpty(grants);
        Assert.Equal(grants.Count, grants.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(grants, x => Assert.Equal(Permissions.Domain.Shared.Enums.PermissionGrantEffect.Allow, x.Effect));

        var roleKey = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");
        var stamp = await stampService.GetAsync(tenantId, PermissionProviderNames.Role, roleKey);
        Assert.True(stamp > 0);
        var cacheKey = PermissionCacheKeys.BuildProviderGrantsKey(
            tenantId, PermissionProviderNames.Role, roleKey, stamp);
        var cached = await grantsCache.GetOrCreateAsync(
            cacheKey,
            () => Task.FromResult(new PermissionProviderGrantsCacheItem()),
            considerUow: false);
        Assert.NotNull(cached);
        Assert.NotEmpty(cached!.AllowExact);
        Assert.Equal(
            grants.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal),
            cached.AllowExact.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Tenant_admin_upsert_should_remove_retired_audit_grants_without_removing_unrelated_custom_grants()
    {
        var tenantId = Guid.NewGuid();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = PermissionsEfCoreTestHarness.BuildSqliteDbContext(
            connection, tenantId: null, multiTenantFilterEnabled: false);
        await dbContext.Database.EnsureCreatedAsync();
        var repository = new EfTestRepository<PermissionGrant, Guid>(dbContext);
        var cacheProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var stampService = new PermissionStampService(
            cacheProvider.GetRequiredService<PermissionProviderStampCache>(),
            new RecordingTenantWrightMessageSender());
        var service = new TenantAdminGrantProvisioningService(
            new PermissionDefinitionManager(new PermissionDefinitionProvider[]
            {
                new TenantPermissionDefinitionProvider(),
                new AuditLogsPermissionDefinitionProvider()
            }),
            repository,
            new PermissionProviderGrantCacheSyncService(
                repository,
                cacheProvider.GetRequiredService<PermissionProviderGrantsCache>(),
                cacheProvider.GetRequiredService<RolePrivilegeMatrixCache>()),
            stampService,
            new TestCurrentTenant(null),
            PermissionsEfCoreTestHarness.AsDbContextProvider(dbContext));

        dbContext.PermissionGrants.AddRange(
            NewTenantAdminGrant(tenantId, AuditLogsPermissions.Read),
            NewTenantAdminGrant(tenantId, AuditLogsPermissions.ReadDetails),
            NewTenantAdminGrant(tenantId, "Custom.TenantAdmin.Permission"));
        await dbContext.SaveChangesAsync();

        await service.UpsertTenantAdminGrantsAsync(tenantId);

        var names = await dbContext.PermissionGrants.AsNoTracking()
            .Where(x => x.TenantId == tenantId &&
                        x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D"))
            .Select(x => x.Name)
            .ToListAsync();
        Assert.DoesNotContain(AuditLogsPermissions.Read, names);
        Assert.DoesNotContain(AuditLogsPermissions.ReadDetails, names);
        Assert.Contains("Custom.TenantAdmin.Permission", names);
    }

    [Fact]
    public async Task Tenant_admin_upsert_should_not_restore_permissions_after_host_has_managed_the_ceiling()
    {
        var tenantId = Guid.NewGuid();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = PermissionsEfCoreTestHarness.BuildSqliteDbContext(
            connection, tenantId: null, multiTenantFilterEnabled: false);
        await dbContext.Database.EnsureCreatedAsync();
        var repository = new EfTestRepository<PermissionGrant, Guid>(dbContext);
        var cacheProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var stampService = new PermissionStampService(
            cacheProvider.GetRequiredService<PermissionProviderStampCache>(),
            new RecordingTenantWrightMessageSender());
        var service = new TenantAdminGrantProvisioningService(
            new PermissionDefinitionManager(new PermissionDefinitionProvider[]
            {
                new TenantPermissionDefinitionProvider()
            }),
            repository,
            new PermissionProviderGrantCacheSyncService(
                repository,
                cacheProvider.GetRequiredService<PermissionProviderGrantsCache>(),
                cacheProvider.GetRequiredService<RolePrivilegeMatrixCache>()),
            stampService,
            new TestCurrentTenant(null),
            PermissionsEfCoreTestHarness.AsDbContextProvider(dbContext));

        var roleKey = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");
        dbContext.PermissionGrants.Add(NewTenantAdminGrant(tenantId, TenantPermissions.Profile.Read));
        dbContext.PermissionAssignmentStates.Add(new PermissionAssignmentState(
            Guid.NewGuid(), tenantId, PermissionProviderNames.Role, roleKey, DateTime.UtcNow));
        await dbContext.SaveChangesAsync();

        await service.UpsertTenantAdminGrantsAsync(tenantId);

        var names = await dbContext.PermissionGrants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == roleKey)
            .Select(x => x.Name)
            .ToListAsync();
        Assert.Equal([TenantPermissions.Profile.Read], names);
        Assert.DoesNotContain(TenantPermissions.Dashboard.Default, names);
    }

    private static PermissionGrant NewTenantAdminGrant(Guid tenantId, string permissionName)
        => new(
            Guid.NewGuid(),
            tenantId,
            permissionName,
            PermissionProviderNames.Role,
            IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D"),
            Permissions.Domain.Shared.Enums.PermissionGrantEffect.Allow,
            DateTime.UtcNow);

    private sealed class RecordingTenantAdminGrantProvisioningService : ITenantAdminGrantProvisioningService
    {
        public Guid? DeletedTenantId { get; private set; }
        public Guid? UpsertedTenantId { get; private set; }

        public Task UpsertTenantAdminGrantsAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            UpsertedTenantId = tenantId;
            return Task.CompletedTask;
        }

        public Task DeleteTenantAdminGrantsAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            DeletedTenantId = tenantId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTenantWrightMessageSender : ITenantWrightMessageSender
    {
        public List<object> Published { get; } = [];

        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class =>
            Task.CompletedTask;

        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
        {
            Published.Add(message);
            return Task.CompletedTask;
        }
    }
}
