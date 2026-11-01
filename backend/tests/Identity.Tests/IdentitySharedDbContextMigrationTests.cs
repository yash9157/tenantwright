using Identity.Application.ApplicationUsers;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.SecuritySettings;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.EntityFrameworkCore.Stores;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Domain;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.Modularity.Context;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Xunit;

namespace Identity.Tests;

public class IdentitySharedDbContextMigrationTests
{
    [Fact]
    public void Identity_module_should_attach_the_request_scoped_context_to_the_persistence_boundary()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=identity;Username=identity;Password=identity"
            })
            .Build();

        new Identity.EntityFrameworkCore.IdentityEntityFrameworkCoreModule().ConfigureServices(
            new ServiceConfigurationContext(services, configuration));

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IPersistenceBoundaryInitializer) &&
                          descriptor.ImplementationType == typeof(EfCorePersistenceBoundaryInitializer<IdentityDbContextApp>) &&
                          descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task Every_mapped_tenant_entity_should_have_a_global_query_filter()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext(
            new IdentityTestSupport.TestCurrentTenant(Guid.NewGuid()));

        var unfilteredTenantTypes = dbContext.Model.GetEntityTypes()
            .Where(entityType => !entityType.IsOwned())
            .Where(entityType => typeof(IMultiTenant).IsAssignableFrom(entityType.ClrType))
            .Where(entityType => entityType.GetRootType().GetDeclaredQueryFilters().Count == 0)
            .Select(entityType => entityType.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unfilteredTenantTypes);
    }

    [Fact]
    public async Task Security_logs_should_be_isolated_by_the_ambient_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantA);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);

        dbContext.IdentitySecurityLogs.Add(new IdentitySecurityLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            Action = "TenantA",
            CreationTime = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
        using (currentTenant.Change(tenantB))
        {
            dbContext.IdentitySecurityLogs.Add(new IdentitySecurityLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                Action = "TenantB",
                CreationTime = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var visible = await dbContext.IdentitySecurityLogs.AsNoTracking().ToListAsync();

        Assert.Single(visible);
        Assert.Equal(tenantA, visible[0].TenantId);
        Assert.Equal(2, await dbContext.IdentitySecurityLogs.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Relational_security_log_query_should_enforce_the_ambient_tenant_filter()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantA);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = IdentityTestSupport.CreateSqliteDbContext(connection, currentTenant);
        await dbContext.Database.EnsureCreatedAsync();

        dbContext.IdentitySecurityLogs.Add(CreateSecurityLog(tenantA, "TenantA"));
        await dbContext.SaveChangesAsync();
        using (currentTenant.Change(tenantB))
        {
            dbContext.IdentitySecurityLogs.Add(CreateSecurityLog(tenantB, "TenantB"));
            await dbContext.SaveChangesAsync();
        }

        var visible = await dbContext.IdentitySecurityLogs.AsNoTracking().ToListAsync();

        Assert.Collection(visible, log =>
        {
            Assert.Equal(tenantA, log.TenantId);
            Assert.Equal("TenantA", log.Action);
        });
        Assert.Equal(2, await dbContext.IdentitySecurityLogs.IgnoreQueryFilters().CountAsync());
    }

    private static IdentitySecurityLog CreateSecurityLog(Guid tenantId, string action)
        => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Action = action,
            CreationTime = DateTime.UtcNow
        };

    [Fact]
    public void IdentityDbContextApp_Should_Inherit_SharedDbContext()
    {
        var baseType = typeof(IdentityDbContextApp).BaseType;
        Assert.NotNull(baseType);
        Assert.True(baseType!.IsGenericType);
        Assert.Equal(typeof(SharedDbContext<>), baseType.GetGenericTypeDefinition());
    }

    [Fact]
    public async Task UserStore_Should_Track_User_Create_When_AutoSave_Disabled()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContextApp>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var dbContext = new IdentityDbContextApp(options);
        using var store = new EfApplicationUserStore(dbContext, new FakeCurrentTenant())
        {
            AutoSaveChanges = false
        };

        var tenantId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        var result = await store.CreateAsync(user, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(EntityState.Added, dbContext.Entry(user).State);
        Assert.IsAssignableFrom<IQueryableUserStore<ApplicationUser>>(store);
        Assert.IsAssignableFrom<IUserRoleStore<ApplicationUser>>(store);
    }

    [Fact]
    public async Task RoleStore_Should_Track_Role_Create_When_AutoSave_Disabled()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContextApp>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var dbContext = new IdentityDbContextApp(options);
        using var store = new EfApplicationRoleStore(dbContext, new FakeCurrentTenant())
        {
            AutoSaveChanges = false
        };

        var role = new ApplicationRole("admin")
        {
            Id = Guid.NewGuid(),
            NormalizedName = "ADMIN",
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        var result = await store.CreateAsync(role, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(EntityState.Added, dbContext.Entry(role).State);
        Assert.IsAssignableFrom<IQueryableRoleStore<ApplicationRole>>(store);
        Assert.IsAssignableFrom<IRoleClaimStore<ApplicationRole>>(store);
    }

    [Fact]
    public async Task IdentityDbContextApp_Should_Map_PublicInvitation_Entity_And_Activation_Flag()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContextApp>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var dbContext = new IdentityDbContextApp(options);

        var invitationEntity = dbContext.Model.FindEntityType(typeof(IdentityPublicInvitation));

        Assert.NotNull(invitationEntity);
        Assert.Equal("PublicInvitations", invitationEntity!.GetTableName());
        Assert.NotNull(invitationEntity.FindProperty(nameof(IdentityPublicInvitation.TenantName)));
        Assert.NotNull(invitationEntity.FindProperty(nameof(IdentityPublicInvitation.CanceledAtUtc)));
        Assert.NotNull(invitationEntity.FindProperty(nameof(IdentityPublicInvitation.CanceledByUserId)));
        Assert.NotNull(invitationEntity.FindProperty(nameof(IdentityPublicInvitation.CancelReason)));
        Assert.Contains(
            invitationEntity.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Count == 1 &&
                     index.Properties[0].Name == nameof(IdentityPublicInvitation.TokenHash));

        var userEntity = dbContext.Model.FindEntityType(typeof(ApplicationUser));

        Assert.NotNull(userEntity);
        Assert.NotNull(userEntity!.FindProperty(nameof(ApplicationUser.RequireEmailActivationBeforeLogin)));
        Assert.NotNull(userEntity.FindProperty(nameof(ApplicationUser.LifecycleStatus)));
        Assert.Equal(128, userEntity.FindProperty(nameof(ApplicationUser.Name))!.GetMaxLength());
        Assert.Equal(128, userEntity.FindProperty(nameof(ApplicationUser.Surname))!.GetMaxLength());
        Assert.Contains(
            userEntity.GetIndexes(),
            index => index.Properties.Select(x => x.Name).SequenceEqual(
                [nameof(ApplicationUser.TenantId), nameof(ApplicationUser.LifecycleStatus)]));
        Assert.Contains(
            userEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual(
                [nameof(ApplicationUser.TenantId), nameof(ApplicationUser.NormalizedUserName)]));
        Assert.Contains(
            userEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual(
                [nameof(ApplicationUser.TenantId), nameof(ApplicationUser.NormalizedEmail)]));
        Assert.Contains(
            userEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual(
                [nameof(ApplicationUser.TenantId), nameof(ApplicationUser.PhoneNumber)]));
        Assert.DoesNotContain(
            userEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Count == 1 &&
                     index.Properties[0].Name is nameof(ApplicationUser.NormalizedUserName)
                         or nameof(ApplicationUser.Email)
                         or nameof(ApplicationUser.PhoneNumber));

        var lifecycleHistoryEntity = dbContext.Model.FindEntityType(typeof(IdentityUserLifecycleHistory));
        Assert.NotNull(lifecycleHistoryEntity);
        Assert.Equal("UserLifecycleHistory", lifecycleHistoryEntity!.GetTableName());
        Assert.Empty(lifecycleHistoryEntity.GetForeignKeys());
        Assert.Contains(
            lifecycleHistoryEntity.GetIndexes(),
            index => index.Properties.Select(x => x.Name).SequenceEqual(
                [
                    nameof(IdentityUserLifecycleHistory.TenantId),
                    nameof(IdentityUserLifecycleHistory.UserId),
                    nameof(IdentityUserLifecycleHistory.EffectiveAtUtc)
                ]));

        var securityLogEntity = dbContext.Model.FindEntityType(typeof(IdentitySecurityLog));
        Assert.NotNull(securityLogEntity);
        Assert.NotNull(securityLogEntity!.FindProperty(nameof(IdentitySecurityLog.RiskLevel)));
        Assert.NotNull(securityLogEntity.FindProperty(nameof(IdentitySecurityLog.RiskAction)));
        Assert.NotNull(securityLogEntity.FindProperty(nameof(IdentitySecurityLog.RiskScore)));
        Assert.NotNull(securityLogEntity.FindProperty(nameof(IdentitySecurityLog.BlockedByRisk)));
        Assert.NotNull(securityLogEntity.FindProperty(nameof(IdentitySecurityLog.FalsePositive)));
        Assert.NotNull(securityLogEntity.FindProperty(nameof(IdentitySecurityLog.CountryCode)));
        Assert.NotNull(securityLogEntity.FindProperty(nameof(IdentitySecurityLog.RiskOverrideId)));
        Assert.Equal(256, securityLogEntity.FindProperty(nameof(IdentitySecurityLog.UserName))!.GetMaxLength());
        Assert.Equal(256, securityLogEntity.FindProperty(nameof(IdentitySecurityLog.Identity))!.GetMaxLength());
        Assert.Equal(256, securityLogEntity.FindProperty(nameof(IdentitySecurityLog.ApplicationName))!.GetMaxLength());
        Assert.Contains(
            securityLogEntity.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(
                [
                    nameof(IdentitySecurityLog.TenantId),
                    nameof(IdentitySecurityLog.CreationTime),
                    nameof(IdentitySecurityLog.Id)
                ]));

        var sessionEntity = dbContext.Model.FindEntityType(typeof(IdentitySession));
        Assert.NotNull(sessionEntity);
        Assert.NotNull(sessionEntity!.FindProperty(nameof(IdentitySession.Status)));
        Assert.NotNull(sessionEntity.FindProperty(nameof(IdentitySession.RevokedAtUtc)));
        Assert.NotNull(sessionEntity.FindProperty(nameof(IdentitySession.RevokedByUserId)));
        Assert.NotNull(sessionEntity.FindProperty(nameof(IdentitySession.RevocationSource)));
        Assert.NotNull(sessionEntity.FindProperty(nameof(IdentitySession.RevocationReason)));
        Assert.Contains(
            sessionEntity.GetIndexes(),
            index => index.Properties.Count == 2 &&
                     index.Properties[0].Name == nameof(IdentitySession.TenantId) &&
                     index.Properties[1].Name == nameof(IdentitySession.Status));

        var hostSecurityPolicyEntity = dbContext.Model.FindEntityType(typeof(HostIdentitySecurityPolicy));
        Assert.NotNull(hostSecurityPolicyEntity);
        Assert.Equal("HostIdentitySecurityPolicies", hostSecurityPolicyEntity!.GetTableName());
        Assert.NotNull(hostSecurityPolicyEntity.FindProperty(nameof(HostIdentitySecurityPolicy.ConcurrencyStamp)));
        Assert.NotNull(hostSecurityPolicyEntity.FindProperty(nameof(HostIdentitySecurityPolicy.PasswordRequiredLength)));
        Assert.NotNull(hostSecurityPolicyEntity.FindProperty(nameof(HostIdentitySecurityPolicy.LoginRiskEnabled)));

        var preferenceEntity = dbContext.Model.FindEntityType(typeof(IdentityUserNotificationSecurityPreference));
        Assert.NotNull(preferenceEntity);
        Assert.Equal("UserNotificationSecurityPreferences", preferenceEntity!.GetTableName());
        Assert.NotNull(preferenceEntity.FindProperty(nameof(IdentityUserNotificationSecurityPreference.EmailEnabled)));
        Assert.NotNull(preferenceEntity.FindProperty(nameof(IdentityUserNotificationSecurityPreference.NewDeviceSignInAlertEnabled)));
        Assert.Contains(
            preferenceEntity.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Count == 2 &&
                     index.Properties[0].Name == nameof(IdentityUserNotificationSecurityPreference.TenantId) &&
                     index.Properties[1].Name == nameof(IdentityUserNotificationSecurityPreference.UserId));
    }

    [Fact]
    public async Task Security_log_identifiers_should_be_truncated_before_the_database_write()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var oversized = new string('x', IdentitySecurityLog.IndexedIdentifierMaxLength + 100);
        var log = new IdentitySecurityLog
        {
            Id = Guid.NewGuid(),
            ApplicationName = oversized,
            Identity = oversized,
            UserName = oversized,
            CreationTime = DateTime.UtcNow
        };

        dbContext.IdentitySecurityLogs.Add(log);
        await dbContext.SaveChangesAsync();

        Assert.Equal(IdentitySecurityLog.IndexedIdentifierMaxLength, log.ApplicationName!.Length);
        Assert.Equal(IdentitySecurityLog.IndexedIdentifierMaxLength, log.Identity!.Length);
        Assert.Equal(IdentitySecurityLog.IndexedIdentifierMaxLength, log.UserName!.Length);
    }

    [Fact]
    public async Task Stores_Should_Resolve_Roles_Per_Tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantA);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);

        var roleA = new ApplicationRole(TenantWrightIdentityRoleNames.TenantAdmin)
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            NormalizedName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant(),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        var roleB = new ApplicationRole(TenantWrightIdentityRoleNames.TenantAdmin)
        {
            Id = Guid.NewGuid(),
            TenantId = tenantB,
            NormalizedName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant(),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        dbContext.Roles.Add(roleA);

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            UserName = "tenant.admin",
            NormalizedUserName = "TENANT.ADMIN",
            Email = "tenant.admin@example.com",
            NormalizedEmail = "TENANT.ADMIN@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesOnDbContextAsync(true);
        using (currentTenant.Change(tenantB))
        {
            dbContext.Roles.Add(roleB);
            await dbContext.SaveChangesOnDbContextAsync(true);
        }

        using var roleStore = new EfApplicationRoleStore(dbContext, currentTenant)
        {
            AutoSaveChanges = false
        };

        using var userStore = new EfApplicationUserStore(dbContext, currentTenant)
        {
            AutoSaveChanges = false
        };

        var resolvedRole = await roleStore.FindByNameAsync(
            TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant(),
            CancellationToken.None);
        Assert.NotNull(resolvedRole);
        Assert.Equal(tenantA, resolvedRole!.TenantId);

        await userStore.AddToRoleAsync(
            user,
            TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant(),
            CancellationToken.None);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var userRole = await dbContext.UserRoles.SingleAsync();
        Assert.Equal(roleA.Id, userRole.RoleId);
    }

    private sealed class FakeCurrentTenant : ICurrentTenant
    {
        public FakeCurrentTenant(Guid? id = null, string? name = null)
        {
            Id = id;
            Name = name;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name { get; private set; }

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            var previousName = Name;
            Id = id;
            Name = name;
            return new RestoreAction(() =>
            {
                Id = previousId;
                Name = previousName;
            });
        }

        private sealed class RestoreAction : IDisposable
        {
            private readonly Action _restore;

            public RestoreAction(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                _restore();
            }
        }
    }
}
