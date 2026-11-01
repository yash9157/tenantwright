using Identity.Application.Contracts.Mutations;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Users;
using Identity.Application.Contracts.Platform;
using Identity.Application.Platform;
using Identity.Application.Users;
using Identity.Controllers.Users;
using Identity.Controllers.Platform;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Shared.CurrentUser;
using Shared.Data.Exceptions;
using Shared.Permissions.Definitions.Identity;
using Xunit;

namespace Identity.Tests;

public sealed class IdentityUserLifecycleTests
{
    [Fact]
    public void Domain_should_enforce_lifecycle_transitions_and_IsActive_compatibility()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), IsActive = true };
        user.InitializeLifecycle(isActive: true);

        user.TransitionLifecycle(IdentityUserLifecycleStatus.Suspended);
        Assert.Equal(IdentityUserLifecycleStatus.Suspended, user.LifecycleStatus);
        Assert.False(user.IsActive);

        Assert.Throws<InvalidOperationException>(() =>
            user.TransitionLifecycle(IdentityUserLifecycleStatus.Archived));

        user.TransitionLifecycle(IdentityUserLifecycleStatus.Terminated);
        user.TransitionLifecycle(IdentityUserLifecycleStatus.Archived);
        user.TransitionLifecycle(IdentityUserLifecycleStatus.Active);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void Controller_should_use_exact_lifecycle_permissions()
    {
        AssertPolicy(nameof(UsersController.SuspendAsync), IdentityPermissions.Users.Suspend);
        AssertPolicy(nameof(UsersController.RestoreAsync), IdentityPermissions.Users.Restore);
        AssertPolicy(nameof(UsersController.TerminateAsync), IdentityPermissions.Users.Terminate);
        AssertPolicy(nameof(UsersController.RehireAsync), IdentityPermissions.Users.Rehire);
        AssertPolicy(nameof(UsersController.ArchiveAsync), IdentityPermissions.Users.Archive);
        AssertPolicy(nameof(UsersController.GetLifecycleHistoryAsync), IdentityPermissions.Users.LifecycleHistory);
        AssertPolicy(nameof(PlatformTenantUsersController.LockAsync), IdentityPermissions.Users.Lock, typeof(PlatformTenantUsersController));
        AssertPolicy(nameof(PlatformTenantUsersController.UnlockAsync), IdentityPermissions.Users.Unlock, typeof(PlatformTenantUsersController));

        AssertPlatformPolicy(nameof(PlatformStaffController.SuspendAsync), IdentityPermissions.PlatformStaff.Suspend);
        AssertPlatformPolicy(nameof(PlatformStaffController.RestoreAsync), IdentityPermissions.PlatformStaff.Restore);
        AssertPlatformPolicy(nameof(PlatformStaffController.TerminateAsync), IdentityPermissions.PlatformStaff.Terminate);
        AssertPlatformPolicy(nameof(PlatformStaffController.RehireAsync), IdentityPermissions.PlatformStaff.Rehire);
        AssertPlatformPolicy(nameof(PlatformStaffController.ArchiveAsync), IdentityPermissions.PlatformStaff.Archive);
        AssertPlatformPolicy(nameof(PlatformStaffController.GetLifecycleHistoryAsync), IdentityPermissions.PlatformStaff.LifecycleHistory);
    }

    [Fact]
    public async Task Platform_lifecycle_should_persist_history_and_rehire_same_identity()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
        await using var db = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(db, currentTenant);
        var revocations = new CapturingSessionRevocationService();
        var service = new PlatformStaffAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(db),
            currentTenant,
            currentUser,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            revocations);

        var created = await service.CreateAsync(new PlatformStaffCreateDto
        {
            UserName = "lifecycle.support",
            Email = "lifecycle.support@example.test",
            Password = "Pass123$"
        });

        var terminated = await service.TerminateAsync(created.Id, new IdentityLifecycleTransitionInput
        {
            ConcurrencyStamp = created.ConcurrencyStamp,
            ReasonCode = "employment-ended",
            CorrelationId = "platform-lifecycle-test"
        });
        Assert.Equal(IdentityUserLifecycleStatus.Terminated, terminated.LifecycleStatus);
        Assert.False(terminated.IsActive);

        var archived = await service.ArchiveAsync(created.Id, new IdentityLifecycleTransitionInput
        {
            ConcurrencyStamp = terminated.ConcurrencyStamp,
            ReasonCode = "retention-archive"
        });
        var normalList = await service.GetPagedListAsync(new GetPlatformStaffInput());
        Assert.DoesNotContain(normalList.Items, x => x.Id == created.Id);
        var archivedList = await service.GetPagedListAsync(new GetPlatformStaffInput
        {
            LifecycleStatus = IdentityUserLifecycleStatus.Archived
        });
        Assert.Contains(archivedList.Items, x => x.Id == created.Id);

        var rehired = await service.RehireAsync(created.Id, new IdentityLifecycleTransitionInput
        {
            ConcurrencyStamp = archived.ConcurrencyStamp,
            ReasonCode = "approved-rehire"
        });

        Assert.Equal(created.Id, rehired.Id);
        Assert.Equal(IdentityUserLifecycleStatus.Active, rehired.LifecycleStatus);
        Assert.True(rehired.IsActive);
        Assert.Equal(2, revocations.Calls.Count);

        var history = await service.GetLifecycleHistoryAsync(created.Id, new GetIdentityLifecycleHistoryInput());
        Assert.Equal(3, history.TotalCount);
        Assert.Equal(
            [
                IdentityUserLifecycleStatus.Active,
                IdentityUserLifecycleStatus.Archived,
                IdentityUserLifecycleStatus.Terminated
            ],
            history.Items.Select(x => x.CurrentStatus));
    }

    [Fact]
    public async Task Lifecycle_commands_should_persist_history_revoke_and_reject_stale_writes()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
        await using var db = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(db, currentTenant);
        var revocations = new CapturingSessionRevocationService();
        var service = new UserAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(db),
            currentTenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            revocations,
            new TenantUserManagementGuard(currentUser, currentTenant, userManager),
            currentUser,
            userImportExportCoordinator: null!,
            tenantStore: new IdentityTestSupport.EmptyTenantStore());

        var created = await service.CreateAsync(new UserCreateDto
        {
            UserName = "lifecycle.user",
            Email = "lifecycle.user@example.test",
            Password = "Pass123$"
        });

        var suspended = await service.SuspendAsync(created.Id, new IdentityLifecycleTransitionInput
        {
            ConcurrencyStamp = created.ConcurrencyStamp,
            ReasonCode = "security-review",
            Note = "Access paused during review",
            CorrelationId = "lifecycle-test"
        });

        Assert.Equal(IdentityUserLifecycleStatus.Suspended, suspended.LifecycleStatus);
        Assert.False(suspended.IsActive);
        Assert.Single(revocations.Calls);

        var history = await service.GetLifecycleHistoryAsync(created.Id, new GetIdentityLifecycleHistoryInput());
        var entry = Assert.Single(history.Items);
        Assert.Equal(IdentityUserLifecycleStatus.Active, entry.PreviousStatus);
        Assert.Equal(IdentityUserLifecycleStatus.Suspended, entry.CurrentStatus);
        Assert.Equal("security-review", entry.ReasonCode);
        Assert.Equal("lifecycle-test", entry.CorrelationId);

        await Assert.ThrowsAsync<SharedDbConcurrencyException>(() => service.RestoreAsync(
            created.Id,
            new IdentityLifecycleTransitionInput
            {
                ConcurrencyStamp = created.ConcurrencyStamp,
                ReasonCode = "stale-restore"
            }));
    }

    [Fact]
    public async Task Host_lock_command_requires_reason_honors_concurrency_and_returns_the_refreshed_record()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
        await using var db = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(db, currentTenant);
        var revocations = new CapturingSessionRevocationService();
        var service = new UserAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(db),
            currentTenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            revocations,
            new TenantUserManagementGuard(currentUser, currentTenant, userManager),
            currentUser,
            userImportExportCoordinator: null!,
            tenantStore: new IdentityTestSupport.EmptyTenantStore());
        var created = await service.CreateAsync(new UserCreateDto
        {
            UserName = "locked.user",
            Email = "locked.user@example.test",
            Password = "Pass123$"
        });

        var locked = await service.LockAsync(created.Id, new IdentityLockStateChangeInput
        {
            ConcurrencyStamp = created.ConcurrencyStamp,
            ReasonCode = "suspected-compromise",
            Note = "Review required",
            CorrelationId = "lock-command-test"
        });

        Assert.True(locked.IsLocked);
        Assert.Single(revocations.Calls);
        await Assert.ThrowsAsync<SharedDbConcurrencyException>(() => service.UnlockAsync(
            created.Id,
            new IdentityLockStateChangeInput
            {
                ConcurrencyStamp = created.ConcurrencyStamp,
                ReasonCode = "stale-unlock"
            }));
    }

    private static void AssertPolicy(string methodName, string expectedPolicy, Type? controllerType = null)
    {
        var method = (controllerType ?? typeof(UsersController)).GetMethod(methodName);
        Assert.NotNull(method);
        var policies = method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(x => x.Policy)
            .ToList();
        Assert.Contains(expectedPolicy, policies);
    }

    private static void AssertPlatformPolicy(string methodName, string expectedPolicy)
    {
        var method = typeof(PlatformStaffController).GetMethod(methodName);
        Assert.NotNull(method);
        var policies = method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(x => x.Policy)
            .ToList();
        Assert.Contains(expectedPolicy, policies);
    }

    private sealed class CapturingSessionRevocationService : IIdentitySessionRevocationService
    {
        public List<(Guid UserId, Guid? TenantId, string Action)> Calls { get; } = [];

        public Task<int> RevokeAllAsync(Guid userId, Guid? tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((userId, tenantId, action));
            return Task.FromResult(1);
        }

        public Task<int> RevokeAllByTenantAsync(Guid tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(IReadOnlyCollection<Guid> sessionIds, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BulkRevokeSessionsResultDto());
    }
}
