using System.Security.Claims;
using Shared.CurrentUser;
using Shared.Security;
using Xunit;

namespace Shared.CurrentUser.Tests;

public class CurrentUserTests
{
    [Fact]
    public void Id_should_fallback_to_sub_when_nameidentifier_is_missing()
    {
        var userId = Guid.NewGuid().ToString("D");
        var principal = CreatePrincipal(
            new Claim(SharedClaimTypes.Subject, userId),
            new Claim(SharedClaimTypes.PreferredUserName, "ops-admin"));
        var currentUser = new CurrentUser(new TestCurrentPrincipalAccessor(principal));

        Assert.Equal(Guid.Parse(userId), currentUser.Id);
        Assert.Equal("ops-admin", currentUser.UserName);
        Assert.True(currentUser.IsAuthenticated);
    }

    [Fact]
    public void Should_extract_roles_tenant_and_session_deterministically()
    {
        var tenantId = Guid.NewGuid().ToString("D");
        var principal = CreatePrincipal(
            new Claim(SharedClaimTypes.UserId, Guid.NewGuid().ToString("D")),
            new Claim(SharedClaimTypes.TenantId, tenantId),
            new Claim(SharedClaimTypes.SessionId, "session-1"),
            new Claim(SharedClaimTypes.Role, "admin"),
            new Claim(SharedClaimTypes.RoleAlt, "editor"),
            new Claim(SharedClaimTypes.Role, "admin"),
            new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "viewer"));
        var currentUser = new CurrentUser(new TestCurrentPrincipalAccessor(principal));

        Assert.Equal(Guid.Parse(tenantId), currentUser.TenantId);
        Assert.Equal("session-1", currentUser.SessionId);
        Assert.Equal(3, currentUser.Roles.Length);
        Assert.True(currentUser.IsInRole("admin"));
        Assert.True(currentUser.IsInRole("editor"));
        Assert.True(currentUser.IsInRole("viewer"));
    }

    [Fact]
    public void Claim_cache_should_snapshot_principal_claims_per_instance()
    {
        var identity = new ClaimsIdentity(
            [new Claim(SharedClaimTypes.Subject, Guid.Parse("11111111-1111-1111-1111-111111111111").ToString("D"))],
            "test");
        var principal = new ClaimsPrincipal(identity);
        var currentUser = new CurrentUser(new TestCurrentPrincipalAccessor(principal));

        var firstId = currentUser.Id;

        identity.AddClaim(new Claim(SharedClaimTypes.Subject, Guid.Parse("22222222-2222-2222-2222-222222222222").ToString("D")));
        var secondId = currentUser.Id;

        Assert.Equal(firstId, secondId);
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class TestCurrentPrincipalAccessor : ICurrentPrincipalAccessor
    {
        public TestCurrentPrincipalAccessor(ClaimsPrincipal principal)
        {
            Principal = principal;
        }

        public ClaimsPrincipal Principal { get; private set; }

        public IDisposable Change(ClaimsPrincipal principal)
        {
            var previous = Principal;
            Principal = principal;
            return new RestoreScope(this, previous);
        }

        private sealed class RestoreScope : IDisposable
        {
            private readonly TestCurrentPrincipalAccessor _accessor;
            private readonly ClaimsPrincipal _previous;

            public RestoreScope(TestCurrentPrincipalAccessor accessor, ClaimsPrincipal previous)
            {
                _accessor = accessor;
                _previous = previous;
            }

            public void Dispose()
            {
                _accessor.Principal = _previous;
            }
        }
    }
}
