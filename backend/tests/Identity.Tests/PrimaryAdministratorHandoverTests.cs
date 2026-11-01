using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Mutations;
using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shared.CurrentUser;
using Shared.Data.Exceptions;
using Shared.MultiTenancy;
using Xunit;

namespace Identity.Tests;

public sealed class PrimaryAdministratorHandoverTests
{
    [Fact]
    public async Task Handover_designates_target_preserves_former_admin_role_and_writes_audit_log()
    {
        var fixture = await HandoverFixture.CreateAsync();

        var result = await fixture.Service.HandoverPrimaryAdministratorAsync(new PrimaryAdministratorHandoverInput
        {
            TargetUserId = fixture.Successor.Id,
            TargetConcurrencyStamp = fixture.Successor.ConcurrencyStamp!,
            ExpectedCurrentPrimaryAdministratorUserId = fixture.FormerPrimary.Id,
            ReasonCode = "OperationalHandover",
            Note = "Primary contact changed."
        });

        var formerPrimary = await fixture.Db.Users.IgnoreQueryFilters()
            .SingleAsync(user => user.Id == fixture.FormerPrimary.Id);
        var successor = await fixture.Db.Users.IgnoreQueryFilters()
            .SingleAsync(user => user.Id == fixture.Successor.Id);

        Assert.Equal(fixture.Successor.Id.ToString(), result.PrimaryAdminUserId);
        Assert.False(formerPrimary.IsPrimaryAdministrator);
        Assert.True(successor.IsPrimaryAdministrator);
        Assert.True(await fixture.Db.UserRoles.AnyAsync(membership => membership.UserId == formerPrimary.Id));
        Assert.True(await fixture.Db.UserRoles.AnyAsync(membership => membership.UserId == successor.Id));
        Assert.Contains(await fixture.Db.IdentitySecurityLogs.IgnoreQueryFilters().ToListAsync(), entry =>
            entry.Action == "PrimaryAdministratorHandedOver" && entry.UserId == successor.Id);
    }

    [Fact]
    public async Task Handover_rejects_stale_primary_designation()
    {
        var fixture = await HandoverFixture.CreateAsync();

        await Assert.ThrowsAsync<SharedDbConcurrencyException>(() =>
            fixture.Service.HandoverPrimaryAdministratorAsync(new PrimaryAdministratorHandoverInput
            {
                TargetUserId = fixture.Successor.Id,
                TargetConcurrencyStamp = fixture.Successor.ConcurrencyStamp!,
                ExpectedCurrentPrimaryAdministratorUserId = Guid.NewGuid(),
                ReasonCode = "OperationalHandover"
            }));
    }

    private sealed class HandoverFixture : IAsyncDisposable
    {
        private HandoverFixture(
            IdentityDbContextApp db,
            UserAppService service,
            ApplicationUser formerPrimary,
            ApplicationUser successor)
        {
            Db = db;
            Service = service;
            FormerPrimary = formerPrimary;
            Successor = successor;
        }

        public IdentityDbContextApp Db { get; }
        public UserAppService Service { get; }
        public ApplicationUser FormerPrimary { get; }
        public ApplicationUser Successor { get; }

        public static async Task<HandoverFixture> CreateAsync()
        {
            var tenantId = Guid.NewGuid();
            var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId);
            var currentUser = new IdentityTestSupport.TestCurrentUser(
                Guid.NewGuid(), tenantId, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
            var db = IdentityTestSupport.CreateDbContext(currentTenant);
            var userManager = IdentityTestSupport.CreateUserManager(db, currentTenant);
            var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
            var service = new UserAppService(
                userManager,
                IdentityTestSupport.AsDbContextProvider(db),
                currentTenant,
                new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
                new IdentityTestSupport.NoopIdentitySessionRevocationService(),
                guard,
                currentUser,
                userImportExportCoordinator: null!,
                tenantStore: new IdentityTestSupport.EmptyTenantStore());

            var role = new ApplicationRole(TenantWrightIdentityRoleNames.TenantAdmin)
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                NormalizedName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant(),
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            var formerPrimary = CreateTenantAdministrator(tenantId, "former.primary", true);
            var successor = CreateTenantAdministrator(tenantId, "successor.admin", false);

            db.Roles.Add(role);
            db.Users.AddRange(formerPrimary, successor);
            db.UserRoles.AddRange(
                new IdentityUserRole<Guid> { UserId = formerPrimary.Id, RoleId = role.Id },
                new IdentityUserRole<Guid> { UserId = successor.Id, RoleId = role.Id });
            await db.SaveChangesAsync();

            return new HandoverFixture(db, service, formerPrimary, successor);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static ApplicationUser CreateTenantAdministrator(Guid tenantId, string userName, bool isPrimary)
        {
            return new ApplicationUser
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                Email = $"{userName}@example.test",
                NormalizedEmail = $"{userName}@example.test".ToUpperInvariant(),
                IsActive = true,
                IsPrimaryAdministrator = isPrimary,
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                SecurityStamp = Guid.NewGuid().ToString("N")
            };
        }
    }
}
