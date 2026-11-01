using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// The point of this endpoint is what it refuses to do. A directory search granted to staff who
/// do not hold user management must not become a way to enumerate a tenant's people, reach
/// another tenant, or leak security posture about a colleague — so those are the properties
/// under test, not the happy path alone.
/// </summary>
public class UserDirectoryAppServiceTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Finds_an_active_colleague_by_name_fragment()
    {
        var tenant = new IdentityTestSupport.TestCurrentTenant(TenantA);
        await using var db = IdentityTestSupport.CreateDbContext(tenant);
        db.Users.Add(User(TenantA, "arao", "arao@example.test", "Anita", "Rao"));
        await db.SaveChangesAsync();

        var results = await CreateService(db, tenant).SearchAsync(Query("rao"));

        var match = Assert.Single(results);
        Assert.Equal("Anita Rao", match.DisplayName);
        Assert.Equal("arao@example.test", match.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ra")]
    public async Task Returns_nothing_for_a_query_below_the_minimum_length(string query)
    {
        var tenant = new IdentityTestSupport.TestCurrentTenant(TenantA);
        await using var db = IdentityTestSupport.CreateDbContext(tenant);
        db.Users.Add(User(TenantA, "arao", "arao@example.test", "Anita", "Rao"));
        await db.SaveChangesAsync();

        // A short query must not degrade into "return everything" — that is the enumeration path.
        Assert.Empty(await CreateService(db, tenant).SearchAsync(Query(query)));
    }

    [Fact]
    public async Task Never_returns_a_user_from_another_tenant()
    {
        var tenant = new IdentityTestSupport.TestCurrentTenant(TenantA);
        await using var db = IdentityTestSupport.CreateDbContext(tenant);

        // Seeded under tenant B's own context: SharedDbContext refuses a cross-tenant write, so
        // this data cannot be planted from tenant A even in a test.
        using (tenant.Change(TenantB, "tenant-b"))
        {
            db.Users.Add(User(TenantB, "brao", "brao@other.local", "Bharat", "Rao"));
            await db.SaveChangesAsync();
        }

        Assert.Empty(await CreateService(db, tenant).SearchAsync(Query("rao")));
    }

    [Fact]
    public async Task Fails_closed_when_no_tenant_is_resolved()
    {
        var hostContext = new IdentityTestSupport.TestCurrentTenant(null);
        await using var db = IdentityTestSupport.CreateDbContext(hostContext);
        db.Users.Add(User(TenantA, "arao", "arao@example.test", "Anita", "Rao"));
        await db.SaveChangesAsync();

        // No ambient tenant must mean no results, never a fallback to host-wide search.
        Assert.Empty(await CreateService(db, hostContext).SearchAsync(Query("rao")));
    }

    [Fact]
    public async Task Excludes_inactive_and_non_active_lifecycle_users()
    {
        var tenant = new IdentityTestSupport.TestCurrentTenant(TenantA);
        await using var db = IdentityTestSupport.CreateDbContext(tenant);
        var suspended = User(TenantA, "draosuspended", "d@example.test", "Deepa", "Rao");
        suspended.TransitionLifecycle(IdentityUserLifecycleStatus.Suspended);

        var archived = User(TenantA, "eraoarchived", "e@example.test", "Esha", "Rao");
        archived.TransitionLifecycle(IdentityUserLifecycleStatus.Archived);

        db.Users.Add(suspended);
        db.Users.Add(archived);
        await db.SaveChangesAsync();

        // A suspended or former colleague is not a candidate reviewer.
        Assert.Empty(await CreateService(db, tenant).SearchAsync(Query("rao")));
    }

    [Fact]
    public async Task Clamps_the_result_count_to_the_hard_cap()
    {
        var tenant = new IdentityTestSupport.TestCurrentTenant(TenantA);
        await using var db = IdentityTestSupport.CreateDbContext(tenant);
        for (var i = 0; i < UserDirectoryConsts.MaxResultCount + 15; i++)
        {
            db.Users.Add(User(TenantA, $"rao{i:D3}", $"rao{i:D3}@example.test", "Rao", $"Number{i:D3}"));
        }
        await db.SaveChangesAsync();

        // A caller asking for more than the cap gets the cap, not the directory.
        var results = await CreateService(db, tenant)
            .SearchAsync(new SearchUserDirectoryInput { Query = "rao", MaxResultCount = 5_000 });

        Assert.Equal(UserDirectoryConsts.MaxResultCount, results.Count);
    }

    private static UserDirectoryAppService CreateService(
        IdentityDbContextApp db,
        IdentityTestSupport.TestCurrentTenant tenant) =>
        new(IdentityTestSupport.AsDbContextProvider(db), tenant);

    private static SearchUserDirectoryInput Query(string query) =>
        new() { Query = query, MaxResultCount = UserDirectoryConsts.DefaultResultCount };

    private static ApplicationUser User(
        Guid tenantId,
        string userName,
        string email,
        string? name = null,
        string? surname = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Name = name,
            Surname = surname,
            IsActive = true,
        };
}
