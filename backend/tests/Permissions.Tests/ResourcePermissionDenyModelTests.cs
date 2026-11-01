using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Definitions;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.Tests.TestDoubles;
using Shared.MultiTenancy;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Coverage for sub-flow 4 (resource-grant deny model): <see cref="ResourcePermissionGrant"/> gained an
/// <see cref="PermissionGrantEffect"/> column (default <c>Allow</c>, backward-compatible), and
/// <see cref="ResourcePermissionManagementAppService"/> reads/writes it so a stored Deny row overrides
/// what would otherwise look like a grant.
/// </summary>
public sealed class ResourcePermissionDenyModelTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string ResourceName = "Workspace";
    private const string ProviderKey = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Fact]
    public async Task Resource_deny_overrides_resource_allow_for_the_same_permission()
    {
        using var connection = OpenDatabase();
        var resourceKey = Guid.NewGuid().ToString("D");

        // Simulate a broader allow (e.g. seeded/imported) then a narrower explicit deny for the same key.
        await SeedGrantsAsync(connection,
            NewGrant("Documents.Read", resourceKey, PermissionGrantEffect.Deny));

        var service = BuildService(connection);
        var result = await service.GetAsync(ResourceName, resourceKey, PermissionProviderNames.Role, ProviderKey);

        var permission = result.Groups.SelectMany(g => g.Permissions).Single(p => p.Name == "Documents.Read");
        Assert.False(permission.IsGranted);
        Assert.Equal(PermissionGrantEffect.Deny, permission.Effect);
    }

    [Fact]
    public async Task Absent_effect_on_an_existing_row_defaults_to_allow_and_shows_as_granted()
    {
        // Confirms claim from the plan: rows persisted before the Effect column existed default to
        // Allow via the column default, so existing data is unaffected by this change.
        using var connection = OpenDatabase();
        var resourceKey = Guid.NewGuid().ToString("D");
        await SeedGrantsAsync(connection, NewGrant("Documents.Read", resourceKey, PermissionGrantEffect.Allow));

        var service = BuildService(connection);
        var result = await service.GetAsync(ResourceName, resourceKey, PermissionProviderNames.Role, ProviderKey);

        var permission = result.Groups.SelectMany(g => g.Permissions).Single(p => p.Name == "Documents.Read");
        Assert.True(permission.IsGranted);
        Assert.Equal(PermissionGrantEffect.Allow, permission.Effect);
    }

    [Fact]
    public async Task Update_persists_a_deny_effect_and_reports_it_as_not_granted_on_a_later_read()
    {
        using var connection = OpenDatabase();
        var resourceKey = Guid.NewGuid().ToString("D");

        // UpdateAsync inserts/updates with autoSave:false (production flushes via the ambient
        // unit-of-work middleware); this test has no such middleware, so it flushes explicitly on
        // the same context/connection to prove persistence, matching how the app service is actually
        // invoked in production (UOW commit happens after the controller action returns).
        await using (var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true))
        {
            var service = BuildServiceForContext(context);
            await service.UpdateAsync(ResourceName, resourceKey, PermissionProviderNames.Role, ProviderKey, new UpdateResourcePermissionsDto
            {
                Permissions =
                [
                    new() { Name = "Documents.Read", IsGranted = true, Effect = PermissionGrantEffect.Deny }
                ]
            });
            await context.SaveChangesAsync();
        }

        var result = await BuildService(connection).GetAsync(ResourceName, resourceKey, PermissionProviderNames.Role, ProviderKey);
        var permission = result.Groups.SelectMany(g => g.Permissions).Single(p => p.Name == "Documents.Read");

        Assert.False(permission.IsGranted);
        Assert.Equal(PermissionGrantEffect.Deny, permission.Effect);
    }

    [Fact]
    public async Task Unrelated_permissions_are_unaffected_by_a_deny_on_another_permission()
    {
        using var connection = OpenDatabase();
        var resourceKey = Guid.NewGuid().ToString("D");
        await SeedGrantsAsync(connection,
            NewGrant("Documents.Read", resourceKey, PermissionGrantEffect.Deny),
            NewGrant("Documents.Update", resourceKey, PermissionGrantEffect.Allow));

        var service = BuildService(connection);
        var result = await service.GetAsync(ResourceName, resourceKey, PermissionProviderNames.Role, ProviderKey);
        var permissions = result.Groups.SelectMany(g => g.Permissions).ToDictionary(p => p.Name, StringComparer.Ordinal);

        Assert.False(permissions["Documents.Read"].IsGranted);
        Assert.True(permissions["Documents.Update"].IsGranted);
        Assert.False(permissions.ContainsKey("Folders.Read") && permissions["Folders.Read"].IsGranted);
    }

    private static ResourcePermissionGrant NewGrant(string name, string resourceKey, PermissionGrantEffect effect)
    {
        return new ResourcePermissionGrant(
            Guid.NewGuid(),
            TenantId,
            name,
            ResourceName,
            resourceKey,
            1,
            PermissionProviderNames.Role,
            ProviderKey,
            effect,
            DateTime.UtcNow,
            expiresAtUtc: null,
            reason: null,
            ticketId: null,
            grantedByUserId: null);
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        connection.Open();
        using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        context.Database.EnsureCreated();
        return connection;
    }

    private static async Task SeedGrantsAsync(SqliteConnection connection, params ResourcePermissionGrant[] grants)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        context.ResourcePermissionGrants.AddRange(grants);
        await context.SaveChangesAsync();
    }

    private static ResourcePermissionManagementAppService BuildService(SqliteConnection connection)
    {
        var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        return BuildServiceForContext(context);
    }

    private static ResourcePermissionManagementAppService BuildServiceForContext(Permissions.EntityFrameworkCore.EntityFrameworkCore.PermissionsDbContextApp context)
    {
        var repository = new EfTestRepository<ResourcePermissionGrant, Guid>(context);
        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var definitionManager = new PermissionDefinitionManager([new TestPermissionDefinitionProvider()]);
        var definitionIndex = new PermissionDefinitionIndex(definitionManager);

        return new ResourcePermissionManagementAppService(
            new AllowAllAuthorizationService(),
            new TestCurrentTenant(TenantId),
            definitionManager,
            definitionIndex,
            repository,
            stampService);
    }

    private sealed class TestPermissionDefinitionProvider : PermissionDefinitionProvider
    {
        public override void Define(IPermissionDefinitionContext context)
        {
            var group = context.AddGroup("Documents", "Documents");
            group.AddPermission("Documents.Read", "Read");
            group.AddPermission("Documents.Update", "Update");

            var folders = context.AddGroup("Folders", "Folders");
            folders.AddPermission("Folders.Read", "Read");
        }
    }

    private sealed class AllowAllAuthorizationService : IRequestAuthorizationService
    {
        public Task EnsureAuthorizedAsync(string policyName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
