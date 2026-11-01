using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Identity.Application.Contracts.Account;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Shared.Security;

namespace Auth.Claims;

public sealed class RoleClaimsPrincipalService
{
    private const int ProtectedTokenEnvelopeBudgetBytes = 1024;
    private readonly AccessTokenSecurityOptions _options;

    public RoleClaimsPrincipalService(IOptions<AccessTokenSecurityOptions> options)
    {
        _options = options.Value;
    }

    public bool TryApply(ClaimsPrincipal principal, UserInfoDto userInfo, out string? failureReason)
    {
        failureReason = null;
        var identity = principal.Identity as ClaimsIdentity
                       ?? throw new InvalidOperationException("The token principal must contain a claims identity.");

        foreach (var claim in identity.FindAll(OpenIddictConstants.Claims.Role).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        foreach (var claim in identity.FindAll(SharedClaimTypes.IdentityRoleId).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        if (!principal.HasScope(OpenIddictConstants.Scopes.Roles))
        {
            return true;
        }

        var roleIds = userInfo.RoleIds
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        if (roleIds.Any(x => x == Guid.Empty))
        {
            failureReason = "invalid_identity_role_membership";
            return false;
        }
        if (roleIds.Length > _options.MaxActiveRoleMemberships)
        {
            failureReason = "active_role_membership_limit_exceeded";
            return false;
        }

        foreach (var roleName in userInfo.Roles
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(x => x.Trim())
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            identity.AddClaim(OpenIddictConstants.Claims.Role, roleName);
        }

        foreach (var roleId in roleIds)
        {
            identity.AddClaim(SharedClaimTypes.IdentityRoleId, roleId.ToString("D"));
        }

        if (!IsWithinSerializedTokenBudget(principal))
        {
            failureReason = "access_token_size_limit_exceeded";
            return false;
        }

        return true;
    }

    private bool IsWithinSerializedTokenBudget(ClaimsPrincipal principal)
    {
        var projectedClaims = principal.Claims
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Value, StringComparer.Ordinal)
            .Select(x => new { x.Type, x.Value })
            .ToArray();
        var payloadBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(projectedClaims));
        var base64PayloadBytes = checked(((payloadBytes + 2) / 3) * 4);
        return base64PayloadBytes + ProtectedTokenEnvelopeBudgetBytes <= _options.MaxSerializedAccessTokenBytes;
    }
}
