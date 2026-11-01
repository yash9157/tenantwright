using System.Security.Claims;

namespace Shared.Security;

public static class CurrentPrincipalAccessorExtensions
{
    public static IDisposable Change(this ICurrentPrincipalAccessor accessor, Claim claim)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(claim);

        return accessor.Change(new[] { claim });
    }

    public static IDisposable Change(this ICurrentPrincipalAccessor accessor, IEnumerable<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(claims);

        return accessor.Change(new ClaimsIdentity(claims));
    }

    public static IDisposable Change(this ICurrentPrincipalAccessor accessor, ClaimsIdentity claimsIdentity)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(claimsIdentity);

        return accessor.Change(new ClaimsPrincipal(claimsIdentity));
    }
}
