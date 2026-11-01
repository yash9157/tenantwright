using Identity.Application.ApplicationUsers;
using Identity.Application.InternalPermissionTargets;
using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Identity.Application.Platform;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.CurrentUser;
using Shared.MultiTenancy;
using Xunit;

namespace Identity.Tests;

public sealed class PlatformStaffAndRoleAppServiceTests
{
    [Fact]
    public async Task Staff_list_returns_only_host_users()
    {
        var fixture = CreateFixture();
        await CreateRoleAsync(fixture.RoleManager, "PlatformSupport");
        var hostUser = await CreateUserAsync(fixture.UserManager, "host.user", Guid.Empty, "PlatformSupport");
        await CreateUserAsync(fixture.UserManager, "tenant.user", Guid.NewGuid());

        var result = await fixture.StaffService.GetPagedListAsync(new GetPlatformStaffInput());

        var user = Assert.Single(result.Items);
        Assert.Equal(hostUser.Id.ToString("D"), user.Id);
        Assert.Null(user.TenantId);
        Assert.Equal(["PlatformSupport"], user.Roles);
    }

    [Fact]
    public async Task Staff_create_stores_host_tenant_sentinel()
    {
        var fixture = CreateFixture();
        await CreateRoleAsync(fixture.RoleManager, "PlatformSupport");

        var created = await fixture.StaffService.CreateAsync(new PlatformStaffCreateDto
        {
            UserName = "platform.created",
            Email = "platform.created@example.test",
            Password = "Pass123$",
            RoleNames = ["PlatformSupport"]
        });

        var stored = await fixture.DbContext.Users.SingleAsync(x => x.Id.ToString() == created.Id);
        Assert.Equal(Guid.Empty, stored.TenantId);
        Assert.Null(created.TenantId);
        Assert.Equal(["PlatformSupport"], created.Roles);
    }

    [Theory]
    [InlineData(TenantWrightIdentityRoleNames.HostAdmin)]
    [InlineData(TenantWrightIdentityRoleNames.TenantAdmin)]
    public async Task Staff_create_rejects_reserved_admin_roles(string roleName)
    {
        var fixture = CreateFixture();
        await CreateRoleAsync(fixture.RoleManager, roleName);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.StaffService.CreateAsync(new PlatformStaffCreateDto
        {
            UserName = "platform.blocked",
            Email = "platform.blocked@example.test",
            Password = "Pass123$",
            RoleNames = [roleName]
        }));
    }

    [Fact]
    public async Task Staff_service_requires_host_admin_outside_tenant_context()
    {
        var tenant = new IdentityTestSupport.TestCurrentTenant(Guid.NewGuid());
        var fixture = CreateFixture(tenant, new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenant.Id, "tenant.admin", TenantWrightIdentityRoleNames.TenantAdmin));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.StaffService.GetPagedListAsync(new GetPlatformStaffInput()));
    }

    [Fact]
    public async Task Role_list_returns_only_host_roles()
    {
        var fixture = CreateFixture();
        var hostRole = await CreateRoleAsync(fixture.RoleManager, "PlatformSupport");
        var tenantRole = new ApplicationRole("TenantSupport")
        {
            Id = Guid.NewGuid(),
            NormalizedName = "TENANTSUPPORT",
            TenantId = Guid.NewGuid()
        };
        fixture.DbContext.Roles.Add(tenantRole);
        await fixture.DbContext.SaveChangesAsync();

        var result = await fixture.RoleService.GetListAsync(new PlatformRoleListInput());

        // The tenant-owned role must not appear, and the total must count only what the query returns —
        // a paged endpoint that reports a cross-tenant total would leak the other tenant's existence.
        Assert.Equal(1, result.TotalCount);
        var role = Assert.Single(result.Items);
        Assert.Equal(hostRole.Id.ToString("D"), role.Id);
        Assert.Equal("PlatformSupport", role.Name);
    }

    [Fact]
    public async Task Platform_role_metadata_round_trips_without_changing_tenant_role_behavior()
    {
        var fixture = CreateFixture();

        var created = await fixture.RoleService.CreateAsync(new PlatformRoleCreateDto
        {
            Name = "PlatformSupport",
            Description = "First-line platform support.",
            Category = "Operations",
            IsSensitive = true
        });

        var result = await fixture.RoleService.GetAsync(created.Id);

        Assert.Equal("First-line platform support.", result.Description);
        Assert.Equal("Operations", result.Category);
        Assert.True(result.IsSensitive);
    }

    [Fact]
    public async Task Platform_role_members_and_counts_include_only_platform_staff()
    {
        var fixture = CreateFixture();
        var role = await CreateRoleAsync(fixture.RoleManager, "PlatformSupport");
        var hostUser = await CreateUserAsync(fixture.UserManager, "host.support", Guid.Empty, "PlatformSupport");
        var tenantUser = await CreateUserAsync(fixture.UserManager, "tenant.support", Guid.NewGuid());
        fixture.DbContext.UserRoles.Add(new ApplicationUserRole
        {
            UserId = tenantUser.Id,
            RoleId = role.Id,
            TenantId = tenantUser.TenantId
        });
        await fixture.DbContext.SaveChangesAsync();

        var roleSummary = Assert.Single((await fixture.RoleService.GetListAsync(new PlatformRoleListInput())).Items);
        var members = await fixture.RoleService.GetMembersAsync(role.Id.ToString("D"), new PagedRequestDto());

        Assert.Equal(1, roleSummary.MemberCount);
        var member = Assert.Single(members.Items);
        Assert.Equal(hostUser.Id.ToString("D"), member.Id);
        Assert.Equal(1, members.TotalCount);
    }

    [Theory]
    [InlineData(TenantWrightIdentityRoleNames.HostAdmin)]
    [InlineData(TenantWrightIdentityRoleNames.TenantAdmin)]
    public async Task Role_create_rejects_reserved_admin_roles(string roleName)
    {
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.RoleService.CreateAsync(new PlatformRoleCreateDto
        {
            Name = roleName
        }));
    }

    [Fact]
    public async Task Permission_target_role_lookup_requires_the_exact_host_or_tenant_scope()
    {
        var fixture = CreateFixture();
        var hostRole = await CreateRoleAsync(fixture.RoleManager, "PlatformSupport");
        var tenantRole = new ApplicationRole("TenantSupport")
        {
            Id = Guid.NewGuid(),
            NormalizedName = "TENANTSUPPORT",
            TenantId = Guid.NewGuid()
        };
        fixture.DbContext.Roles.Add(tenantRole);
        await fixture.DbContext.SaveChangesAsync();

        var hostTarget = await fixture.PermissionTargetService.GetRoleAsync(hostRole.Id, tenantId: null);
        var tenantTarget = await fixture.PermissionTargetService.GetRoleAsync(tenantRole.Id, tenantRole.TenantId);
        var wrongScopeTarget = await fixture.PermissionTargetService.GetRoleAsync(tenantRole.Id, tenantId: null);

        Assert.NotNull(hostTarget);
        Assert.Equal(hostRole.Name, hostTarget.Name);
        Assert.False(hostTarget.IsStatic);
        Assert.NotNull(tenantTarget);
        Assert.Equal(tenantRole.Name, tenantTarget.Name);
        Assert.Null(wrongScopeTarget);
    }

    [Fact]
    public async Task Permission_target_role_lookup_marks_reserved_platform_roles_static()
    {
        var fixture = CreateFixture();
        var role = await CreateRoleAsync(fixture.RoleManager, TenantWrightIdentityRoleNames.HostAdmin);

        var target = await fixture.PermissionTargetService.GetRoleAsync(role.Id, tenantId: null);

        Assert.NotNull(target);
        Assert.Equal(TenantWrightIdentityRoleNames.HostAdmin, target.Name);
        Assert.True(target.IsStatic);
    }

    [Fact]
    public async Task Permission_target_user_lookup_enforces_exact_scope_and_returns_active_role_ids()
    {
        var fixture = CreateFixture();
        var role = await CreateRoleAsync(fixture.RoleManager, "PlatformSupport");
        var hostUser = await CreateUserAsync(fixture.UserManager, "host.target", Guid.Empty, "PlatformSupport");
        var tenantId = Guid.NewGuid();
        var tenantUser = await CreateUserAsync(fixture.UserManager, "tenant.target", tenantId);

        var hostTarget = await fixture.PermissionTargetService.GetUserAsync(hostUser.Id, tenantId: null);
        var tenantTarget = await fixture.PermissionTargetService.GetUserAsync(tenantUser.Id, tenantId);
        var wrongScopeTarget = await fixture.PermissionTargetService.GetUserAsync(tenantUser.Id, tenantId: null);

        Assert.NotNull(hostTarget);
        Assert.Equal(hostUser.Id.ToString("D"), hostTarget.Id);
        Assert.Equal([role.Id], hostTarget.RoleIds);
        Assert.NotNull(tenantTarget);
        Assert.Null(wrongScopeTarget);
    }

    [Fact]
    public async Task Permission_assignment_target_search_scopes_users_and_roles_to_the_selected_tenant()
    {
        var fixture = CreateFixture();
        var selectedTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var selectedUser = await CreateUserAsync(fixture.UserManager, "selected.user", selectedTenantId);
        var inactiveUser = await CreateUserAsync(fixture.UserManager, "inactive.user", selectedTenantId);
        inactiveUser.IsActive = false;
        var updateInactiveUser = await fixture.UserManager.UpdateAsync(inactiveUser);
        Assert.True(updateInactiveUser.Succeeded);
        await CreateUserAsync(fixture.UserManager, "other.user", otherTenantId);
        var selectedRole = new ApplicationRole("SelectedReviewer")
        {
            Id = Guid.NewGuid(),
            NormalizedName = "SELECTEDREVIEWER",
            TenantId = selectedTenantId
        };
        var systemRole = new ApplicationRole(TenantWrightIdentityRoleNames.TenantAdmin)
        {
            Id = Guid.NewGuid(),
            NormalizedName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant(),
            TenantId = selectedTenantId
        };
        var otherRole = new ApplicationRole("OtherReviewer")
        {
            Id = Guid.NewGuid(),
            NormalizedName = "OTHERREVIEWER",
            TenantId = otherTenantId
        };
        fixture.DbContext.Roles.AddRange(selectedRole, systemRole, otherRole);
        await fixture.DbContext.SaveChangesAsync();

        var users = await fixture.AssignmentTargetSearchService.GetUsersAsync(
            new PermissionAssignmentTargetSearchInput { TenantId = selectedTenantId });
        var roles = await fixture.AssignmentTargetSearchService.GetRolesAsync(
            new PermissionAssignmentTargetSearchInput { TenantId = selectedTenantId });

        Assert.Equal(selectedUser.Id, Assert.Single(users.Items).Id);
        Assert.Equal(selectedRole.Id, Assert.Single(roles.Items).Id);
        Assert.Equal(1, users.TotalCount);
        Assert.Equal(1, roles.TotalCount);
    }

    private static TestFixture CreateFixture(
        ICurrentTenant? currentTenant = null,
        ICurrentUser? currentUser = null)
    {
        currentTenant ??= new IdentityTestSupport.TestCurrentTenant(null);
        currentUser ??= new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(),
            null,
            "host.admin",
            TenantWrightIdentityRoleNames.HostAdmin);
        var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var cacheInvalidator = new IdentityTestSupport.NoopIdentityAuthCacheInvalidator();

        return new TestFixture(
            dbContext,
            userManager,
            roleManager,
            new PlatformStaffAppService(
                userManager,
                IdentityTestSupport.AsDbContextProvider(dbContext),
                currentTenant,
                currentUser,
                cacheInvalidator,
                new IdentityTestSupport.NoopIdentitySessionRevocationService()),
            new PlatformRoleAppService(
                roleManager,
                IdentityTestSupport.AsDbContextProvider(dbContext),
                currentTenant,
                currentUser,
                cacheInvalidator,
                new IdentityTestSupport.NoopIdentitySessionRevocationService(),
                IdentityTestSupport.CreateNoopMessageSender(),
                Microsoft.Extensions.Options.Options.Create(
                    new Identity.Application.Users.RoleMembershipPolicyOptions())),
            new IdentityPermissionTargetValidationAppService(IdentityTestSupport.AsDbContextProvider(dbContext)),
            new PermissionAssignmentTargetSearchAppService(
                IdentityTestSupport.AsDbContextProvider(dbContext),
                currentTenant,
                currentUser));
    }

    private static async Task<ApplicationRole> CreateRoleAsync(ApplicationRoleManager roleManager, string roleName)
    {
        var role = new ApplicationRole(roleName)
        {
            TenantId = null
        };
        var result = await roleManager.CreateAsync(role);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
        return role;
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        ApplicationUserManager userManager,
        string userName,
        Guid tenantId,
        params string[] roles)
    {
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@example.test",
            TenantId = tenantId,
            IsActive = true
        };
        var result = await userManager.CreateAsync(user, "Pass123$");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));

        if (roles.Length > 0)
        {
            var addRoles = await userManager.AddToRolesAsync(user, roles);
            Assert.True(addRoles.Succeeded, string.Join(", ", addRoles.Errors.Select(x => x.Description)));
        }

        return user;
    }

    private sealed record TestFixture(
        IdentityDbContextApp DbContext,
        ApplicationUserManager UserManager,
        ApplicationRoleManager RoleManager,
        PlatformStaffAppService StaffService,
        PlatformRoleAppService RoleService,
        IdentityPermissionTargetValidationAppService PermissionTargetService,
        PermissionAssignmentTargetSearchAppService AssignmentTargetSearchService);
}
