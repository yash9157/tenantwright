using System.Security.Claims;
using Auth.Claims;
using Identity.Application.Contracts.Account;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Shared.Security;
using Xunit;

namespace Auth.Tests;

public sealed class RoleClaimsPrincipalServiceTests
{
    [Fact]
    public void Applies_sorted_deduplicated_names_and_ids_when_roles_scope_is_granted()
    {
        var principal = CreatePrincipal(includeRolesScope: true);
        var second = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var first = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var userInfo = new UserInfoDto
        {
            Roles = ["Support", "Auditor", "Support"],
            RoleIds = [second, first, second]
        };

        var applied = CreateService().TryApply(principal, userInfo, out var failureReason);

        Assert.True(applied);
        Assert.Null(failureReason);
        Assert.Equal(["Auditor", "Support"], principal.FindAll(OpenIddictConstants.Claims.Role).Select(x => x.Value));
        Assert.Equal(
            [first.ToString("D"), second.ToString("D")],
            principal.FindAll(SharedClaimTypes.IdentityRoleId).Select(x => x.Value));
    }

    [Fact]
    public void Does_not_emit_role_claims_without_roles_scope()
    {
        var principal = CreatePrincipal(includeRolesScope: false);

        Assert.True(CreateService().TryApply(
            principal,
            new UserInfoDto { Roles = ["Support"], RoleIds = [Guid.NewGuid()] },
            out _));
        Assert.Empty(principal.FindAll(OpenIddictConstants.Claims.Role));
        Assert.Empty(principal.FindAll(SharedClaimTypes.IdentityRoleId));
    }

    [Fact]
    public void Rejects_the_thirty_third_role_without_truncating()
    {
        var principal = CreatePrincipal(includeRolesScope: true);
        var roleIds = Enumerable.Range(1, 33)
            .Select(index => Guid.Parse($"00000000-0000-0000-0000-{index:D12}"))
            .ToList();

        var applied = CreateService().TryApply(
            principal,
            new UserInfoDto { RoleIds = roleIds },
            out var failureReason);

        Assert.False(applied);
        Assert.Equal("active_role_membership_limit_exceeded", failureReason);
        Assert.Empty(principal.FindAll(SharedClaimTypes.IdentityRoleId));
    }

    [Fact]
    public void Rejects_a_projected_token_that_exceeds_the_serialized_budget()
    {
        var principal = CreatePrincipal(includeRolesScope: true);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("large", new string('x', 5_000)));

        var applied = CreateService().TryApply(principal, new UserInfoDto(), out var failureReason);

        Assert.False(applied);
        Assert.Equal("access_token_size_limit_exceeded", failureReason);
    }

    private static RoleClaimsPrincipalService CreateService() => new(Options.Create(new AccessTokenSecurityOptions()));

    private static ClaimsPrincipal CreatePrincipal(bool includeRolesScope)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity("test"));
        principal.SetScopes(includeRolesScope
            ? [OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.Roles]
            : [OpenIddictConstants.Scopes.OpenId]);
        return principal;
    }
}
