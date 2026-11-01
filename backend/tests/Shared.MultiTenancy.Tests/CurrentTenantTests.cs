using Shared.MultiTenancy;
using Shared.Security;
using System.Security.Claims;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class CurrentTenantTests
{
    [Fact]
    public void Id_Uses_Legacy_Claim_When_CurrentScope_Not_Set()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(SharedClaimTypes.LegacyTenantId, tenantId.ToString("D"))
        ]));

        var currentTenant = new CurrentTenant(
            new TestCurrentPrincipalAccessor(principal),
            new AsyncLocalCurrentTenantAccessor());

        Assert.Equal(tenantId, currentTenant.Id);
    }

    [Fact]
    public void Change_Sets_And_Restores_Tenant_Context()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var currentTenant = new CurrentTenant(
            new TestCurrentPrincipalAccessor(new ClaimsPrincipal(new ClaimsIdentity())),
            accessor);

        var tenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using (currentTenant.Change(tenantId, "acme"))
        {
            Assert.Equal(tenantId, currentTenant.Id);
            Assert.Equal("acme", currentTenant.Name);
        }

        Assert.Null(currentTenant.Id);
        Assert.Null(currentTenant.Name);
    }

    [Fact]
    public void Claim_fallback_follows_the_current_principal_across_requests()
    {
        var firstTenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var secondTenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var principalAccessor = new TestCurrentPrincipalAccessor(PrincipalFor(firstTenantId));
        var currentTenant = new CurrentTenant(principalAccessor, new AsyncLocalCurrentTenantAccessor());

        Assert.Equal(firstTenantId, currentTenant.Id);

        using (principalAccessor.Change(PrincipalFor(secondTenantId)))
        {
            Assert.Equal(secondTenantId, currentTenant.Id);
        }

        Assert.Equal(firstTenantId, currentTenant.Id);
    }

    [Fact]
    public void Explicit_host_scope_suppresses_principal_tenant_claims_and_restores_them_after_disposal()
    {
        var tenantId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var currentTenant = new CurrentTenant(
            new TestCurrentPrincipalAccessor(PrincipalFor(tenantId)),
            new AsyncLocalCurrentTenantAccessor());

        Assert.Equal(tenantId, currentTenant.Id);

        using (currentTenant.Change(null))
        {
            Assert.False(currentTenant.IsAvailable);
            Assert.Null(currentTenant.Id);
            Assert.Null(currentTenant.Name);
        }

        Assert.Equal(tenantId, currentTenant.Id);
    }

    private static ClaimsPrincipal PrincipalFor(Guid tenantId) => new(new ClaimsIdentity(
    [
        new Claim(SharedClaimTypes.TenantId, tenantId.ToString("D"))
    ]));

    private sealed class TestCurrentPrincipalAccessor : ICurrentPrincipalAccessor
    {
        private ClaimsPrincipal _principal;

        public TestCurrentPrincipalAccessor(ClaimsPrincipal principal)
        {
            _principal = principal;
        }

        public ClaimsPrincipal Principal => _principal;

        public IDisposable Change(ClaimsPrincipal principal)
        {
            var original = _principal;
            _principal = principal;
            return new Restore(() => _principal = original);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Action _restore;

            public Restore(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                _restore();
            }
        }
    }
}
