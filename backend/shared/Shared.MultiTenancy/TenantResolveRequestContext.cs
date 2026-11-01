using System.Security.Claims;

namespace Shared.MultiTenancy;

public sealed class TenantResolveRequestContext
{
    public bool IsAuthenticated { get; init; }

    public IReadOnlyDictionary<string, string[]> Headers { get; init; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Claim> Claims { get; init; } = [];

    public string? Path { get; init; }
}
