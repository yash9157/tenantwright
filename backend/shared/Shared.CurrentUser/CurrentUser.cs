using Shared.Modularity.DependencyInjection;
using Shared.Security;
using System.Security.Claims;
using System.Diagnostics.Metrics;

namespace Shared.CurrentUser;

public class CurrentUser : ICurrentUser, ITransientDependency
{
    private static readonly Claim[] EmptyClaimsArray = [];
    private static readonly Meter Meter = new("Shared.CurrentUser");
    private static readonly Counter<long> ClaimMissCounter =
        Meter.CreateCounter<long>("shared.current_user.claim_miss_total");

    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly object _claimCacheLock = new();

    private Claim[]? _allClaims;
    private Dictionary<string, Claim[]>? _claimsByType;
    private string[]? _roles;

    public virtual bool IsAuthenticated => Id.HasValue;

    public virtual Guid? Id => ClaimValueParser.ToGuidOrNull(
        FindFirstClaimValue("user_id", SharedClaimTypes.UserId, SharedClaimTypes.Subject));

    public virtual string? UserName => FindFirstClaimValue(
        "user_name",
        SharedClaimTypes.PreferredUserName,
        SharedClaimTypes.UserName);

    public virtual string? Name => FindFirstClaimValue(
        "name",
        SharedClaimTypes.Name,
        SharedClaimTypes.NameAlt);

    public virtual string? SurName => FindFirstClaimValue(
        "surname",
        SharedClaimTypes.SurName,
        SharedClaimTypes.SurNameAlt);

    public virtual string? PhoneNumber => FindFirstClaimValue(
        "phone_number",
        SharedClaimTypes.PhoneNumber,
        SharedClaimTypes.PhoneNumberAlt);

    public virtual bool PhoneNumberVerified => ClaimValueParser.ToBooleanOrDefault(
        FindFirstClaimValue("phone_number_verified", SharedClaimTypes.PhoneNumberVerified));

    public virtual string? Email => FindFirstClaimValue("email", SharedClaimTypes.Email);

    public virtual bool EmailVerified => ClaimValueParser.ToBooleanOrDefault(
        FindFirstClaimValue("email_verified", SharedClaimTypes.EmailVerified));

    public virtual Guid? TenantId => ClaimValueParser.ToGuidOrNull(
        FindFirstClaimValue("tenant_id", SharedClaimTypes.TenantId, SharedClaimTypes.LegacyTenantId));

    public virtual string? SessionId => FindFirstClaimValue("session_id", SharedClaimTypes.SessionId);

    public virtual string[] Roles
    {
        get
        {
            EnsureClaimCacheInitialized();

            if (_roles != null)
            {
                return _roles;
            }

            lock (_claimCacheLock)
            {
                if (_roles != null)
                {
                    return _roles;
                }

                _roles = GetAllClaims()
                    .Where(claim =>
                        claim.Type == SharedClaimTypes.Role ||
                        claim.Type == SharedClaimTypes.RoleAlt ||
                        claim.Type.EndsWith("/role", StringComparison.OrdinalIgnoreCase))
                    .Select(claim => claim.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                return _roles;
            }
        }
    }

    public CurrentUser(ICurrentPrincipalAccessor principalAccessor)
    {
        _principalAccessor = principalAccessor;
    }

    public virtual Claim? FindClaim(string claimType)
    {
        ArgumentNullException.ThrowIfNull(claimType);

        var claims = FindClaims(claimType);
        return claims.Length == 0 ? null : claims[0];
    }

    public virtual Claim[] FindClaims(string claimType)
    {
        ArgumentNullException.ThrowIfNull(claimType);
        EnsureClaimCacheInitialized();
        return _claimsByType!.GetValueOrDefault(claimType) ?? EmptyClaimsArray;
    }

    public virtual Claim[] GetAllClaims()
    {
        EnsureClaimCacheInitialized();
        return _allClaims!;
    }

    public virtual bool IsInRole(string roleName)
    {
        ArgumentNullException.ThrowIfNull(roleName);
        return Roles.Any(role => string.Equals(role, roleName, StringComparison.Ordinal));
    }

    private string? FindFirstClaimValue(string metricClaimKey, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var claim = FindClaim(claimType);
            if (claim != null && !string.IsNullOrWhiteSpace(claim.Value))
            {
                return claim.Value;
            }
        }

        ClaimMissCounter.Add(1, new KeyValuePair<string, object?>("claim", metricClaimKey));
        return null;
    }

    private void EnsureClaimCacheInitialized()
    {
        if (_claimsByType != null)
        {
            return;
        }

        lock (_claimCacheLock)
        {
            if (_claimsByType != null)
            {
                return;
            }

            _allClaims = _principalAccessor.Principal.Claims.ToArray();

            var claimsByType = new Dictionary<string, List<Claim>>(StringComparer.Ordinal);
            foreach (var claim in _allClaims)
            {
                if (!claimsByType.TryGetValue(claim.Type, out var list))
                {
                    list = new List<Claim>();
                    claimsByType[claim.Type] = list;
                }

                list.Add(claim);
            }

            _claimsByType = claimsByType.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
        }
    }
}
