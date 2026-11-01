using Identity.Application.Contracts.OrganizationUnits;
using Identity.Application.OrganizationUnits;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Identity.Tests;

public class OrganizationUnitAppServiceTests
{
    [Fact]
    public async Task Organization_unit_members_should_be_listed_and_removed_with_tenant_scope()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var organizationUnit = CreateOrganizationUnit(tenantId, "00001", "Field Operations");
        var user = CreateUser(tenantId, "field.admin", "field.admin@example.com");
        var otherTenantUser = CreateUser(otherTenantId, "other.admin", "other.admin@example.com");
        var role = CreateRole(tenantId, "FieldAdmin");
        var otherTenantRole = CreateRole(otherTenantId, "OtherWardAdmin");

        dbContext.OrganizationUnits.Add(organizationUnit);
        dbContext.Users.Add(user);
        dbContext.Roles.Add(role);
        await dbContext.SaveChangesAsync();
        using (currentTenant.Change(otherTenantId, "tenant-b"))
        {
            dbContext.Users.Add(otherTenantUser);
            dbContext.Roles.Add(otherTenantRole);
            await dbContext.SaveChangesAsync();
        }

        var service = CreateService(dbContext, currentTenant);

        await service.AddUserAsync(organizationUnit.Id, user.Id.ToString());
        await service.AddRoleAsync(organizationUnit.Id, role.Id.ToString());
        using (currentTenant.Change(otherTenantId, "tenant-b"))
        {
            dbContext.UserOrganizationUnits.Add(new UserOrganizationUnit
            {
                OrganizationUnitId = organizationUnit.Id,
                UserId = otherTenantUser.Id,
                TenantId = otherTenantId
            });
            dbContext.OrganizationUnitRoles.Add(new OrganizationUnitRole
            {
                OrganizationUnitId = organizationUnit.Id,
                RoleId = otherTenantRole.Id,
                TenantId = otherTenantId
            });
            await dbContext.SaveChangesAsync();
        }

        var users = await service.GetUsersAsync(organizationUnit.Id);
        var roles = await service.GetRolesAsync(organizationUnit.Id);

        var listedUser = Assert.Single(users);
        Assert.Equal(user.Id.ToString(), listedUser.Id);
        Assert.Equal(user.UserName, listedUser.UserName);
        Assert.Equal(user.Email, listedUser.Email);
        Assert.True(listedUser.IsActive);

        var listedRole = Assert.Single(roles);
        Assert.Equal(role.Id.ToString(), listedRole.Id);
        Assert.Equal(role.Name, listedRole.Name);

        await service.RemoveUserAsync(organizationUnit.Id, user.Id.ToString());
        await service.RemoveRoleAsync(organizationUnit.Id, role.Id.ToString());

        Assert.Empty(await service.GetUsersAsync(organizationUnit.Id));
        Assert.Empty(await service.GetRolesAsync(organizationUnit.Id));
    }

    [Fact]
    public async Task Organization_unit_member_operations_should_reject_invalid_ids()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var organizationUnit = CreateOrganizationUnit(tenantId, "00001", "Field Operations");
        dbContext.OrganizationUnits.Add(organizationUnit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, currentTenant);

        await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            service.AddUserAsync(organizationUnit.Id, "not-a-guid"));
        await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            service.AddRoleAsync(organizationUnit.Id, "not-a-guid"));
        await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            service.RemoveUserAsync(organizationUnit.Id, "not-a-guid"));
        await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            service.RemoveRoleAsync(organizationUnit.Id, "not-a-guid"));
    }

    private static OrganizationUnitAppService CreateService(
        IdentityDbContextApp dbContext,
        IdentityTestSupport.TestCurrentTenant currentTenant)
    {
        return new OrganizationUnitAppService(IdentityTestSupport.AsDbContextProvider(dbContext), currentTenant);
    }

    private static OrganizationUnit CreateOrganizationUnit(Guid tenantId, string code, string displayName)
    {
        return new OrganizationUnit
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Code = code,
            DisplayName = displayName
        };
    }

    private static ApplicationUser CreateUser(Guid tenantId, string userName, string email)
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsActive = true,
            EmailConfirmed = true
        };
    }

    private static ApplicationRole CreateRole(Guid tenantId, string name)
    {
        return new ApplicationRole
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            NormalizedName = name.ToUpperInvariant()
        };
    }
}
