using Microsoft.AspNetCore.Authorization;

namespace Shared.AspNetCore.Host;

public static class SharedAuthorizationDefaults
{
    public static void Configure(AuthorizationOptions options)
    {
        var fallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        options.FallbackPolicy = fallbackPolicy;
    }
}
