using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Identity.Controllers.LinkUser;
using Identity.Controllers.UserDelegation;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Permissions.Definitions.Identity;
using Xunit;

namespace Identity.Tests;

public class IdentityDelegationAndLinkUserAppServiceTests
{
    [Fact]
    public async Task DelegateNewUserAsync_should_create_tenant_scoped_delegation_with_metadata()
    {
        var tenantId = Guid.NewGuid();
        var sourceUser = CreateUser(tenantId, "source.user", "source@example.com");
        var targetUser = CreateUser(tenantId, "target.user", "target@example.com");

        await using var dbContext = IdentityTestSupport.CreateDbContext(new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));
        dbContext.Users.AddRange(sourceUser, targetUser);
        await dbContext.SaveChangesAsync();

        var service = new IdentityUserDelegationAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentUser(sourceUser.Id, tenantId, sourceUser.UserName),
            new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));

        var now = DateTime.UtcNow;
        await service.DelegateNewUserAsync(new CreateUserDelegationDto
        {
            TargetUserId = targetUser.Id,
            StartTime = now.AddMinutes(-5),
            EndTime = now.AddHours(2)
        });

        var delegations = await service.GetListAsync();

        var delegation = Assert.Single(delegations);
        Assert.Equal(sourceUser.Id, delegation.SourceUserId);
        Assert.Equal(sourceUser.UserName, delegation.SourceUserName);
        Assert.Equal(sourceUser.Email, delegation.SourceEmail);
        Assert.Equal(targetUser.Id, delegation.TargetUserId);
        Assert.Equal(targetUser.UserName, delegation.TargetUserName);
        Assert.Equal(targetUser.Email, delegation.TargetEmail);
        Assert.True(delegation.IsActive);
        Assert.False(delegation.IsExpired);
    }

    [Fact]
    public async Task DelegateNewUserAsync_should_reject_cross_tenant_target_and_overlapping_duplicate()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var sourceUser = CreateUser(tenantId, "source.user", "source@example.com");
        var targetUser = CreateUser(tenantId, "target.user", "target@example.com");
        var crossTenantTarget = CreateUser(otherTenantId, "other.target", "other@example.com");

        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        dbContext.Users.AddRange(sourceUser, targetUser);
        await dbContext.SaveChangesAsync();
        using (currentTenant.Change(otherTenantId, "tenant-b"))
        {
            dbContext.Users.Add(crossTenantTarget);
            await dbContext.SaveChangesAsync();
        }

        var service = new IdentityUserDelegationAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentUser(sourceUser.Id, tenantId, sourceUser.UserName),
            currentTenant);

        var now = DateTime.UtcNow;
        await Assert.ThrowsAsync<SharedValidationException>(() => service.DelegateNewUserAsync(new CreateUserDelegationDto
        {
            TargetUserId = crossTenantTarget.Id,
            StartTime = now,
            EndTime = now.AddHours(1)
        }));

        await service.DelegateNewUserAsync(new CreateUserDelegationDto
        {
            TargetUserId = targetUser.Id,
            StartTime = now,
            EndTime = now.AddHours(1)
        });

        await Assert.ThrowsAsync<SharedValidationException>(() => service.DelegateNewUserAsync(new CreateUserDelegationDto
        {
            TargetUserId = targetUser.Id,
            StartTime = now.AddMinutes(15),
            EndTime = now.AddHours(2)
        }));
    }

    [Fact]
    public async Task Link_requests_should_require_target_approval_before_creating_active_link()
    {
        var tenantId = Guid.NewGuid();
        var requester = CreateUser(tenantId, "requester", "requester@example.com");
        var approver = CreateUser(tenantId, "approver", "approver@example.com");

        await using var dbContext = IdentityTestSupport.CreateDbContext(new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));
        dbContext.Users.AddRange(requester, approver);
        await dbContext.SaveChangesAsync();

        var requesterService = new IdentityLinkUserAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentUser(requester.Id, tenantId, requester.UserName),
            new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));

        await requesterService.CreateRequestAsync(new CreateLinkUserRequestInput
        {
            TargetUserId = approver.Id
        });

        Assert.Empty(await requesterService.GetListAsync());

        var requesterRequests = await requesterService.GetRequestsAsync();
        var pendingRequest = Assert.Single(requesterRequests);
        Assert.Equal("Pending", pendingRequest.Status);
        Assert.False(pendingRequest.IsIncoming);
        Assert.Equal(approver.Email, pendingRequest.TargetEmail);

        var approverService = new IdentityLinkUserAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentUser(approver.Id, tenantId, approver.UserName),
            new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));

        var approverRequests = await approverService.GetRequestsAsync();
        var incomingRequest = Assert.Single(approverRequests);
        Assert.True(incomingRequest.IsIncoming);
        Assert.Equal(requester.Email, incomingRequest.RequestedByEmail);

        await approverService.ApproveRequestAsync(incomingRequest.Id);

        var requesterLinks = await requesterService.GetListAsync();
        var requesterLink = Assert.Single(requesterLinks);
        Assert.Equal(approver.Id, requesterLink.CounterpartUserId);
        Assert.Equal(approver.UserName, requesterLink.CounterpartUserName);
        Assert.Equal(approver.Email, requesterLink.CounterpartEmail);

        var approverLinks = await approverService.GetListAsync();
        var approverLink = Assert.Single(approverLinks);
        Assert.Equal(requester.Id, approverLink.CounterpartUserId);
    }

    [Fact]
    public async Task Link_requests_should_reject_cross_tenant_targets()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var requester = CreateUser(tenantId, "requester", "requester@example.com");
        var target = CreateUser(otherTenantId, "other-user", "other@example.com");

        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        dbContext.Users.Add(requester);
        await dbContext.SaveChangesAsync();
        using (currentTenant.Change(otherTenantId, "tenant-b"))
        {
            dbContext.Users.Add(target);
            await dbContext.SaveChangesAsync();
        }

        var service = new IdentityLinkUserAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentUser(requester.Id, tenantId, requester.UserName),
            currentTenant);

        await Assert.ThrowsAsync<SharedValidationException>(() => service.CreateRequestAsync(new CreateLinkUserRequestInput
        {
            TargetUserId = target.Id
        }));
    }

    [Fact]
    public async Task Admin_link_user_operations_should_act_for_selected_user_within_current_tenant()
    {
        var tenantId = Guid.NewGuid();
        var selectedUser = CreateUser(tenantId, "selected.user", "selected@example.com");
        var targetUser = CreateUser(tenantId, "target.user", "target@example.com");

        await using var dbContext = IdentityTestSupport.CreateDbContext(new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));
        dbContext.Users.AddRange(selectedUser, targetUser);
        await dbContext.SaveChangesAsync();

        var service = new IdentityLinkUserAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenantId, "admin"),
            new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));

        await service.CreateRequestForUserAsync(new CreateLinkUserRequestForUserInput
        {
            SourceUserId = selectedUser.Id,
            TargetUserId = targetUser.Id
        });

        var selectedRequests = await service.GetRequestsForUserAsync(selectedUser.Id);
        var pendingRequest = Assert.Single(selectedRequests);
        Assert.False(pendingRequest.IsIncoming);
        Assert.Equal(targetUser.Id, pendingRequest.TargetUserId);

        var targetRequests = await service.GetRequestsForUserAsync(targetUser.Id);
        var incomingRequest = Assert.Single(targetRequests);
        Assert.True(incomingRequest.IsIncoming);

        await service.ApproveRequestForUserAsync(incomingRequest.Id, new LinkUserRequestAdminDecisionInput
        {
            UserId = targetUser.Id
        });

        var links = await service.GetListForUserAsync(selectedUser.Id);
        var link = Assert.Single(links);
        Assert.Equal(targetUser.Id, link.CounterpartUserId);

        await service.UnlinkForUserAsync(new LinkUserAdminUnlinkInput
        {
            SourceUserId = selectedUser.Id,
            TargetUserId = targetUser.Id
        });

        Assert.Empty(await service.GetListForUserAsync(selectedUser.Id));
    }

    [Fact]
    public async Task Admin_delegation_operations_should_act_for_selected_source_user()
    {
        var tenantId = Guid.NewGuid();
        var selectedUser = CreateUser(tenantId, "selected.user", "selected@example.com");
        var targetUser = CreateUser(tenantId, "target.user", "target@example.com");

        await using var dbContext = IdentityTestSupport.CreateDbContext(new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));
        dbContext.Users.AddRange(selectedUser, targetUser);
        await dbContext.SaveChangesAsync();

        var service = new IdentityUserDelegationAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenantId, "admin"),
            new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));

        var now = DateTime.UtcNow;
        await service.DelegateNewUserForSourceAsync(new CreateUserDelegationForSourceDto
        {
            SourceUserId = selectedUser.Id,
            TargetUserId = targetUser.Id,
            StartTime = now.AddMinutes(-5),
            EndTime = now.AddHours(1)
        });

        var assigned = await service.GetListForSourceAsync(selectedUser.Id);
        var delegation = Assert.Single(assigned);
        Assert.Equal(selectedUser.Id, delegation.SourceUserId);
        Assert.Equal(targetUser.Id, delegation.TargetUserId);

        var incoming = await service.GetActiveDelegationsForTargetAsync(targetUser.Id);
        Assert.Single(incoming);

        await service.DeleteDelegationForSourceAsync(delegation.Id, selectedUser.Id);

        Assert.Empty(await service.GetListForSourceAsync(selectedUser.Id));
    }

    [Fact]
    public void Admin_identity_operation_endpoints_should_require_admin_permissions()
    {
        AssertEndpointPolicy(
            typeof(LinkUserController),
            nameof(LinkUserController.GetListForUserAsync),
            IdentityPermissions.Users.Update);
        AssertEndpointPolicy(
            typeof(LinkUserController),
            nameof(LinkUserController.GetRequestsForUserAsync),
            IdentityPermissions.Users.Update);
        AssertEndpointPolicy(
            typeof(LinkUserController),
            nameof(LinkUserController.CreateRequestForUserAsync),
            IdentityPermissions.Users.Update);
        AssertEndpointPolicy(
            typeof(LinkUserController),
            nameof(LinkUserController.ApproveRequestForUserAsync),
            IdentityPermissions.Users.Update);
        AssertEndpointPolicy(
            typeof(LinkUserController),
            nameof(LinkUserController.RejectRequestForUserAsync),
            IdentityPermissions.Users.Update);
        AssertEndpointPolicy(
            typeof(LinkUserController),
            nameof(LinkUserController.UnlinkForUserAsync),
            IdentityPermissions.Users.Update);

        AssertEndpointPolicy(
            typeof(UserDelegationController),
            nameof(UserDelegationController.GetListForSourceAsync),
            IdentityPermissions.UserDelegation.Manage);
        AssertEndpointPolicy(
            typeof(UserDelegationController),
            nameof(UserDelegationController.GetActiveDelegationsForTargetAsync),
            IdentityPermissions.UserDelegation.Manage);
        AssertEndpointPolicy(
            typeof(UserDelegationController),
            nameof(UserDelegationController.DelegateNewUserForSourceAsync),
            IdentityPermissions.UserDelegation.Manage);
        AssertEndpointPolicy(
            typeof(UserDelegationController),
            nameof(UserDelegationController.DeleteDelegationForSourceAsync),
            IdentityPermissions.UserDelegation.Manage);
    }

    private static void AssertEndpointPolicy(Type controllerType, string methodName, string expectedPolicy)
    {
        var method = controllerType.GetMethods()
            .Single(x => x.Name == methodName);

        var authorizeAttribute = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>());
        Assert.Equal(expectedPolicy, authorizeAttribute.Policy);
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
}
