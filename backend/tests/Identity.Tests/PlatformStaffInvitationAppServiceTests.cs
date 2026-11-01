using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Invitations;
using Identity.Application.Invitations;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Shared;
using Xunit;

namespace Identity.Tests;

public sealed class PlatformStaffInvitationAppServiceTests
{
    [Fact]
    public async Task Platform_staff_invitation_should_be_host_scoped_server_addressed_and_queued_for_delivery()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(),
            null,
            "host.admin",
            TenantWrightIdentityRoleNames.HostAdmin);
        var createRole = await roleManager.CreateAsync(new ApplicationRole("PlatformOperator") { TenantId = null });
        Assert.True(createRole.Succeeded, string.Join(", ", createRole.Errors.Select(x => x.Description)));

        var publisher = new IdentityTestSupport.CapturingMessageSender();
        var service = new InvitationAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            roleManager,
            new TenantUserManagementGuard(currentUser, currentTenant, userManager),
            currentUser,
            currentTenant,
            Options.Create(new PublicInvitationManagementOptions
            {
                Enabled = true,
                TokenTtlHours = 24,
                TemplateName = "Identity.PublicInvitationLink",
                Subject = "Join TenantWright",
                AllowedBaseUrls = ["https://localhost:7239"]
            }),
            publisher);

        var created = await service.CreatePlatformStaffAsync(new CreatePlatformStaffInvitationInput
        {
            Email = "platform.operator@example.test",
            RoleName = "PlatformOperator",
            SuggestedUserName = "platform.operator"
        });
        var listed = await service.GetPlatformStaffListAsync(new PlatformStaffInvitationListInput());

        Assert.Equal(Guid.Empty, created.TenantId);
        Assert.Equal("TenantWright platform", created.TenantName);
        Assert.Equal("pending", created.Status);
        Assert.Equal("Queued", created.DeliveryState);
        Assert.Equal(created.Id, Assert.Single(listed.Items).Id);
        var request = Assert.Single(publisher.Requests);
        Assert.StartsWith("https://localhost:7239/auth/accept-invite?token=", request.TemplateModel["link"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Platform_staff_invitation_should_not_assign_host_administrator()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(),
            null,
            "host.admin",
            TenantWrightIdentityRoleNames.HostAdmin);
        var service = new InvitationAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            roleManager,
            new TenantUserManagementGuard(currentUser, currentTenant, userManager),
            currentUser,
            currentTenant,
            Options.Create(new PublicInvitationManagementOptions
            {
                Enabled = true,
                TokenTtlHours = 24,
                TemplateName = "Identity.PublicInvitationLink",
                Subject = "Join TenantWright",
                AllowedBaseUrls = ["https://localhost:7239"]
            }),
            new IdentityTestSupport.CapturingMessageSender());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreatePlatformStaffAsync(
            new CreatePlatformStaffInvitationInput
            {
                Email = "not.allowed@example.test",
                RoleName = TenantWrightIdentityRoleNames.HostAdmin
            }));
    }
}
