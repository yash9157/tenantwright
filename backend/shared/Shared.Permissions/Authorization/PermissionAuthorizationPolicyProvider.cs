using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Shared.Permissions.Client;

namespace Shared.Permissions.Authorization;

public class PermissionAuthorizationPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;
    private readonly IOptions<SharedPermissionClientOptions> _permissionOptions;

    public PermissionAuthorizationPolicyProvider(
        IOptions<AuthorizationOptions> options,
        IOptions<SharedPermissionClientOptions> permissionOptions)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
        _permissionOptions = permissionOptions;
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
    {
        return _fallback.GetDefaultPolicyAsync();
    }

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
    {
        return _fallback.GetFallbackPolicyAsync();
    }

    public async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var policy = await _fallback.GetPolicyAsync(policyName);
        if (policy != null)
        {
            return policy;
        }

        if (!_permissionOptions.Value.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(policyName))
        {
            return null;
        }

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionAuthorizationRequirement(policyName))
            .Build();
    }
}
