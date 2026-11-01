using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Users;
using Identity.Application.Contracts.Mutations;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.CurrentUser;
using Shared.Data.Exceptions;
using Shared.MultiTenancy;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Coverage for the enriched user list/detail projection and derived activity (P1 + P2):
/// email-confirmed/MFA/pending-email/concurrency-stamp fields, detail-only fields, the new
/// server filters, batched last-login/last-activity derivation, and optimistic-concurrency on
/// update.
/// </summary>
public sealed class UserProjectionAndActivityTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public async Task List_projects_enriched_fields()
    {
        var f = CreateFixture();
        var user = await CreateUserAsync(f, "alice");
        user.EmailConfirmed = true;
        user.TwoFactorEnabled = true;
        user.PendingEmail = "alice.new@example.test";
        await f.Db.SaveChangesAsync();

        var page = await f.Service.GetPagedListAsync(new GetUsersInput { TenantId = Tenant, MaxResultCount = 100 });

        var item = Assert.Single(page.Items);
        Assert.True(item.EmailConfirmed);
        Assert.True(item.TwoFactorEnabled);
        Assert.True(item.HasPendingEmail);
        Assert.False(string.IsNullOrWhiteSpace(item.ConcurrencyStamp));
    }

    [Fact]
    public async Task Detail_projects_detail_only_fields_and_session_count()
    {
        var f = CreateFixture();
        var user = await CreateUserAsync(f, "bob");
        user.PendingEmail = "bob.new@example.test";
        user.PendingEmailRequestedAtUtc = DateTime.UtcNow;
        user.ShouldChangePasswordOnNextLogin = true;
        await f.Db.SaveChangesAsync();
        AddSession(f, user.Id, DateTime.UtcNow, IdentitySessionStatus.Active);
        AddSession(f, user.Id, DateTime.UtcNow, IdentitySessionStatus.Revoked);
        await f.Db.SaveChangesAsync();

        var detail = await f.Service.GetAsync(user.Id.ToString());

        Assert.Equal("bob.new@example.test", detail.PendingEmail);
        Assert.True(detail.HasPendingEmail);
        Assert.True(detail.ShouldChangePasswordOnNextLogin);
        Assert.Equal(1, detail.ActiveSessionCount); // only the Active session counts
    }

    [Fact]
    public async Task List_derives_last_login_and_last_activity_per_user_in_one_batch()
    {
        var f = CreateFixture();
        var alice = await CreateUserAsync(f, "alice");
        var bob = await CreateUserAsync(f, "bob");

        var t1 = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 7, 5, 9, 30, 0, DateTimeKind.Utc);
        AddLoginSuccess(f, alice.Id, t1);
        AddLoginSuccess(f, alice.Id, t2); // later login wins
        AddSession(f, alice.Id, new DateTime(2026, 7, 6, 10, 0, 0, DateTimeKind.Utc), IdentitySessionStatus.Active);
        // Bob: no login-success logs, one session only.
        AddSession(f, bob.Id, new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc), IdentitySessionStatus.Active);
        await f.Db.SaveChangesAsync();

        var page = await f.Service.GetPagedListAsync(new GetUsersInput { TenantId = Tenant, MaxResultCount = 100 });

        var aliceDto = Assert.Single(page.Items, x => x.UserName == "alice");
        var bobDto = Assert.Single(page.Items, x => x.UserName == "bob");
        Assert.Equal(t2, aliceDto.LastLoginAtUtc);
        Assert.Equal(new DateTime(2026, 7, 6, 10, 0, 0, DateTimeKind.Utc), aliceDto.LastActiveAtUtc);
        Assert.Null(bobDto.LastLoginAtUtc);
        Assert.Equal(new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc), bobDto.LastActiveAtUtc);
    }

    [Fact]
    public async Task Filters_narrow_by_email_confirmed_mfa_and_locked()
    {
        var f = CreateFixture();
        var confirmed = await CreateUserAsync(f, "confirmed");
        confirmed.EmailConfirmed = true;
        var mfa = await CreateUserAsync(f, "mfa");
        mfa.TwoFactorEnabled = true;
        var locked = await CreateUserAsync(f, "locked");
        locked.LockoutEnabled = true;
        locked.LockoutEnd = DateTimeOffset.UtcNow.AddDays(1);
        await f.Db.SaveChangesAsync();

        var byEmail = await f.Service.GetPagedListAsync(new GetUsersInput { TenantId = Tenant, EmailConfirmed = true, MaxResultCount = 100 });
        Assert.Equal("confirmed", Assert.Single(byEmail.Items).UserName);

        var byMfa = await f.Service.GetPagedListAsync(new GetUsersInput { TenantId = Tenant, TwoFactorEnabled = true, MaxResultCount = 100 });
        Assert.Equal("mfa", Assert.Single(byMfa.Items).UserName);

        var byLocked = await f.Service.GetPagedListAsync(new GetUsersInput { TenantId = Tenant, IsLocked = true, MaxResultCount = 100 });
        Assert.Equal("locked", Assert.Single(byLocked.Items).UserName);
    }

    [Fact]
    public async Task Directory_filters_and_sorts_by_recorded_created_and_last_login_instants()
    {
        var f = CreateFixture();
        var older = await CreateUserAsync(f, "older");
        var newer = await CreateUserAsync(f, "newer");
        var olderCreatedAtUtc = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var newerCreatedAtUtc = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        older.CreatedAtUtc = olderCreatedAtUtc;
        newer.CreatedAtUtc = newerCreatedAtUtc;
        AddLoginSuccess(f, older.Id, new DateTime(2026, 7, 3, 8, 0, 0, DateTimeKind.Utc));
        AddLoginSuccess(f, newer.Id, new DateTime(2026, 7, 8, 8, 0, 0, DateTimeKind.Utc));
        await f.Db.SaveChangesAsync();

        var createdRange = await f.Service.GetPagedListAsync(new GetUsersInput
        {
            TenantId = Tenant,
            CreatedFromUtc = newerCreatedAtUtc.AddDays(-1),
            MaxResultCount = 100
        });
        var createdSort = await f.Service.GetPagedListAsync(new GetUsersInput
        {
            TenantId = Tenant,
            Sorting = "created asc",
            MaxResultCount = 100
        });
        var lastLoginRange = await f.Service.GetPagedListAsync(new GetUsersInput
        {
            TenantId = Tenant,
            LastLoginToUtc = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc),
            MaxResultCount = 100
        });

        Assert.Equal("newer", Assert.Single(createdRange.Items).UserName);
        Assert.Equal(["older", "newer"], createdSort.Items.Select(user => user.UserName));
        Assert.Equal(olderCreatedAtUtc, createdSort.Items[0].CreatedAtUtc);
        Assert.Equal("older", Assert.Single(lastLoginRange.Items).UserName);
    }

    [Fact]
    public async Task Host_directory_lists_only_tenant_users_and_projects_verified_tenant_names()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
        await using var db = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(db, currentTenant);
        var tenantStore = new IdentityTestSupport.TestTenantStore(
            new TenantConfiguration { Id = tenantA, Name = "North Organization", NormalizedName = "NORTH TENANTWRIGHT" },
            new TenantConfiguration { Id = tenantB, Name = "South Organization", NormalizedName = "SOUTH TENANTWRIGHT" });
        var service = new UserAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(db),
            currentTenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            new IdentityTestSupport.NoopIdentitySessionRevocationService(),
            new TenantUserManagementGuard(currentUser, currentTenant, userManager),
            currentUser,
            userImportExportCoordinator: null!,
            tenantStore);

        db.Users.AddRange(
            CreateDirectoryUser(tenantA, "north.user"),
            CreateDirectoryUser(tenantB, "south.user"),
            CreateDirectoryUser(Guid.Empty, "platform.user"));
        await db.SaveChangesAsync();

        var page = await service.GetHostPagedListAsync(new GetHostTenantUsersInput { MaxResultCount = 100 });

        Assert.Equal(2, page.TotalCount);
        Assert.Collection(
            page.Items.OrderBy(user => user.UserName),
            user =>
            {
                Assert.Equal("north.user", user.UserName);
                Assert.Equal("North Organization", user.TenantName);
            },
            user =>
            {
                Assert.Equal("south.user", user.UserName);
                Assert.Equal("South Organization", user.TenantName);
            });
    }

    [Fact]
    public async Task Update_rejects_stale_concurrency_stamp_but_allows_matching_or_omitted()
    {
        var f = CreateFixture();
        var user = await CreateUserAsync(f, "carol");
        var currentStamp = user.ConcurrencyStamp;

        // Stale stamp -> rejected.
        await Assert.ThrowsAsync<SharedDbConcurrencyException>(() => f.Service.UpdateAsync(
            user.Id.ToString(),
            new UserUpdateDto { Email = user.Email!, IsActive = true, ConcurrencyStamp = "stale-" + Guid.NewGuid() }));

        // Matching stamp -> succeeds.
        var updated = await f.Service.UpdateAsync(
            user.Id.ToString(),
            new UserUpdateDto { Email = user.Email!, IsActive = true, ConcurrencyStamp = currentStamp });
        Assert.NotNull(updated);

        // Omitted stamp remains supported on the focused state command.
        var again = await f.Service.DeactivateAsync(
            user.Id.ToString(),
            new IdentityActiveStateChangeInput { ReasonCode = "test-deactivate" });
        Assert.False(again.IsActive);
    }

    // ---- helpers ----

    private static Fixture CreateFixture()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(Tenant);
        var currentUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
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
        return new Fixture(db, userManager, service);
    }

    private static async Task<ApplicationUser> CreateUserAsync(Fixture f, string userName)
    {
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@example.test",
            TenantId = Tenant,
            IsActive = true
        };
        var result = await f.UserManager.CreateAsync(user, "Pass123$");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
        return user;
    }

    private static void AddSession(Fixture f, Guid userId, DateTime lastActiveUtc, IdentitySessionStatus status)
    {
        f.Db.IdentitySessions.Add(new IdentitySession
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            UserId = userId,
            CreationTime = lastActiveUtc,
            LastActiveTime = lastActiveUtc,
            Status = status,
            SessionId = Guid.NewGuid().ToString("N")
        });
    }

    private static void AddLoginSuccess(Fixture f, Guid userId, DateTime whenUtc)
    {
        f.Db.IdentitySecurityLogs.Add(new IdentitySecurityLog
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            UserId = userId,
            Action = IdentityUserActivityQuery.LoginSuccessAction,
            CreationTime = whenUtc
        });
    }

    private static ApplicationUser CreateDirectoryUser(Guid tenantId, string userName)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = $"{userName}@example.test",
            NormalizedEmail = $"{userName}@example.test".ToUpperInvariant(),
            CreatedAtUtc = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc),
            IsActive = true
        };
        user.InitializeLifecycle(isActive: true);
        return user;
    }

    private sealed record Fixture(
        IdentityDbContextApp Db,
        ApplicationUserManager UserManager,
        UserAppService Service);
}
