using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Identity.Application.Platform;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.CurrentUser;
using Shared.MultiTenancy;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Behavioral tests for administrator-facing profile-name assignment
/// (Identity admin profile names design, 2026-07-11). Administrators can now
/// set the existing <c>Name</c>/<c>Surname</c> fields when creating or updating
/// tenant users and platform staff; the values flow through the shared
/// <see cref="IdentityProfileName"/> normalizer so admin and self-service paths
/// behave identically (trim, empty/whitespace clears to null, length cap).
/// </summary>
public sealed class IdentityAdminProfileNameTests
{
    private const string TrimmedName = "Alice";
    private const string TrimmedSurname = "Ng";

    // ---- shared normalizer contract (also governs the self-service path) ----

    [Fact]
    public void Normalize_trims_surrounding_whitespace()
    {
        Assert.Equal(TrimmedName, IdentityProfileName.Normalize("  Alice  ", "Name"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_maps_empty_or_whitespace_to_null(string? value)
    {
        Assert.Null(IdentityProfileName.Normalize(value, "Name"));
    }

    [Fact]
    public void Normalize_rejects_values_over_max_length()
    {
        var tooLong = new string('a', IdentityProfileName.MaxLength + 1);
        Assert.Throws<SharedValidationException>(() => IdentityProfileName.Normalize(tooLong, "Name"));
    }

    [Fact]
    public void Normalize_accepts_value_at_max_length()
    {
        var atLimit = new string('a', IdentityProfileName.MaxLength);
        Assert.Equal(atLimit, IdentityProfileName.Normalize(atLimit, "Name"));
    }

    // ---- primary-admin display-name composition ----

    [Fact]
    public void ComposeDisplayName_prefers_full_name_when_present()
    {
        Assert.Equal("Alice Ng", UserAppService.ComposeDisplayName("Alice", "Ng", "alice.admin"));
        Assert.Equal("Alice", UserAppService.ComposeDisplayName("Alice", null, "alice.admin"));
        Assert.Equal("Ng", UserAppService.ComposeDisplayName("  ", "Ng", "alice.admin"));
    }

    [Fact]
    public void ComposeDisplayName_falls_back_to_user_name_when_no_name_parts()
    {
        Assert.Equal("alice.admin", UserAppService.ComposeDisplayName(null, null, "alice.admin"));
        Assert.Equal("alice.admin", UserAppService.ComposeDisplayName("  ", "  ", "alice.admin"));
    }

    // ---- tenant user create/update ----

    [Fact]
    public async Task Tenant_user_create_persists_trimmed_names()
    {
        var fixture = CreateTenantFixture(out var tenantId);

        var created = await fixture.UserService.CreateAsync(new UserCreateDto
        {
            UserName = "tenant.user",
            Email = "tenant.user@example.test",
            Password = "Pass123$",
            Name = "  Alice  ",
            Surname = "  Ng  "
        });

        Assert.Equal(TrimmedName, created.Name);
        Assert.Equal(TrimmedSurname, created.Surname);

        var stored = await fixture.DbContext.Users.SingleAsync(x => x.Id.ToString() == created.Id);
        Assert.Equal(TrimmedName, stored.Name);
        Assert.Equal(TrimmedSurname, stored.Surname);
        Assert.Equal(tenantId, stored.TenantId);
    }

    [Fact]
    public async Task Tenant_user_create_without_names_leaves_them_null()
    {
        var fixture = CreateTenantFixture(out _);

        var created = await fixture.UserService.CreateAsync(new UserCreateDto
        {
            UserName = "tenant.noname",
            Email = "tenant.noname@example.test",
            Password = "Pass123$"
        });

        Assert.Null(created.Name);
        Assert.Null(created.Surname);
    }

    [Fact]
    public async Task Tenant_user_create_rejects_overlong_name()
    {
        var fixture = CreateTenantFixture(out _);

        await Assert.ThrowsAsync<SharedValidationException>(() => fixture.UserService.CreateAsync(new UserCreateDto
        {
            UserName = "tenant.toolong",
            Email = "tenant.toolong@example.test",
            Password = "Pass123$",
            Name = new string('a', IdentityProfileName.MaxLength + 1)
        }));
    }

    [Fact]
    public async Task Tenant_user_update_sets_names()
    {
        var fixture = CreateTenantFixture(out _);
        var created = await fixture.UserService.CreateAsync(new UserCreateDto
        {
            UserName = "tenant.update",
            Email = "tenant.update@example.test",
            Password = "Pass123$"
        });

        var updated = await fixture.UserService.UpdateAsync(created.Id, new UserUpdateDto
        {
            Email = "tenant.update@example.test",
            IsActive = true,
            Name = "Alice",
            Surname = "Ng"
        });

        Assert.Equal(TrimmedName, updated.Name);
        Assert.Equal(TrimmedSurname, updated.Surname);
    }

    [Fact]
    public async Task Tenant_user_update_with_whitespace_clears_names()
    {
        var fixture = CreateTenantFixture(out _);
        var created = await fixture.UserService.CreateAsync(new UserCreateDto
        {
            UserName = "tenant.clear",
            Email = "tenant.clear@example.test",
            Password = "Pass123$",
            Name = "Alice",
            Surname = "Ng"
        });

        var cleared = await fixture.UserService.UpdateAsync(created.Id, new UserUpdateDto
        {
            Email = "tenant.clear@example.test",
            IsActive = true,
            Name = "   ",
            Surname = null
        });

        Assert.Null(cleared.Name);
        Assert.Null(cleared.Surname);
    }

    // ---- platform staff create/update ----

    [Fact]
    public async Task Platform_staff_create_persists_trimmed_names()
    {
        var fixture = CreatePlatformFixture();

        var created = await fixture.StaffService.CreateAsync(new PlatformStaffCreateDto
        {
            UserName = "platform.named",
            Email = "platform.named@example.test",
            Password = "Pass123$",
            Name = "  Alice  ",
            Surname = "  Ng  "
        });

        Assert.Equal(TrimmedName, created.Name);
        Assert.Equal(TrimmedSurname, created.Surname);

        var stored = await fixture.DbContext.Users.SingleAsync(x => x.Id.ToString() == created.Id);
        Assert.Equal(TrimmedName, stored.Name);
        Assert.Equal(TrimmedSurname, stored.Surname);
    }

    [Fact]
    public async Task Platform_staff_update_updates_and_clears_names()
    {
        var fixture = CreatePlatformFixture();
        var created = await fixture.StaffService.CreateAsync(new PlatformStaffCreateDto
        {
            UserName = "platform.update",
            Email = "platform.update@example.test",
            Password = "Pass123$",
            Name = "Alice",
            Surname = "Ng"
        });

        var updated = await fixture.StaffService.UpdateAsync(created.Id, new PlatformStaffUpdateDto
        {
            Email = "platform.update@example.test",
            IsActive = true,
            Name = "Bob",
            Surname = "   "
        });

        Assert.Equal("Bob", updated.Name);
        Assert.Null(updated.Surname);
    }

    // ---- fixtures ----

    private static TenantFixture CreateTenantFixture(out Guid tenantId)
    {
        tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), tenantId, "tenant.admin", TenantWrightIdentityRoleNames.TenantAdmin);
        var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);

        var service = new UserAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentTenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            new IdentityTestSupport.NoopIdentitySessionRevocationService(),
            guard,
            currentUser,
            userImportExportCoordinator: null!,
            tenantStore: new IdentityTestSupport.EmptyTenantStore());

        return new TenantFixture(dbContext, service);
    }

    private static PlatformFixture CreatePlatformFixture()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
        var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);

        var service = new PlatformStaffAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentTenant,
            currentUser,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            new IdentityTestSupport.NoopIdentitySessionRevocationService());

        return new PlatformFixture(dbContext, service);
    }

    private sealed record TenantFixture(
        Identity.EntityFrameworkCore.EntityFrameworkCore.IdentityDbContextApp DbContext,
        UserAppService UserService);

    private sealed record PlatformFixture(
        Identity.EntityFrameworkCore.EntityFrameworkCore.IdentityDbContextApp DbContext,
        PlatformStaffAppService StaffService);
}
