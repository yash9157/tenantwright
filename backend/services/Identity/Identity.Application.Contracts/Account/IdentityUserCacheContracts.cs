using System.Security.Cryptography;
using System.Text;

namespace Identity.Application.Contracts.Account;

public static class IdentityUserCacheContracts
{
    public const string ProfileCacheName = "IdentityProfileCacheEnvelope";
    public const string AuthorizationCacheName = "IdentityAuthorizationCacheEnvelope";
    public const string AliasCacheName = "IdentityAliasCacheEnvelope";

    public static string BuildProfileLogicalKey(Guid? tenantId, Guid userId)
        => $"idn:{ToTenantPart(tenantId)}:{userId:D}:profile:v1";

    public static string BuildAuthorizationLogicalKey(Guid? tenantId, Guid userId)
        => $"idn:{ToTenantPart(tenantId)}:{userId:D}:authz:v1";

    public static string BuildAliasLogicalKey(Guid? tenantId, string loginInput)
        => BuildAliasLogicalKeyByHash(tenantId, ComputeAliasHash(loginInput));

    public static string BuildAliasLogicalKeyByHash(Guid? tenantId, string aliasHash)
        => $"idn:{ToTenantPart(tenantId)}:{aliasHash}:uid:v1";

    public static string BuildLockLogicalKey(Guid? tenantId, Guid userId, string segment)
        => $"idn:lock:{ToTenantPart(tenantId)}:{userId:D}:{segment}";

    public static string NormalizeAlias(string alias)
        => alias.Trim().ToUpperInvariant();

    public static string ComputeAliasHash(string alias)
    {
        var normalized = NormalizeAlias(alias);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static List<string> BuildAliasHashes(params string?[] aliases)
    {
        return aliases
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ComputeAliasHash(x!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string ToTenantPart(Guid? tenantId)
        => tenantId?.ToString("D") ?? "host";
}
