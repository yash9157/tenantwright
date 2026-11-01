using Microsoft.AspNetCore.Authorization;

namespace Permissions.Authorization;

public sealed class ScopeAuthorizationRequirement : IAuthorizationRequirement
{
    public IReadOnlyList<string> RequiredScopes { get; }

    public ScopeAuthorizationRequirement(params string[] requiredScopes)
    {
        RequiredScopes = requiredScopes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
