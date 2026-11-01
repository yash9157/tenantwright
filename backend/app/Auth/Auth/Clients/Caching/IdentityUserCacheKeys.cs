using Identity.Application.Contracts.Account;

namespace Auth.Clients.Caching;

public static class IdentityUserCacheKeys
{
    public static string Profile(Guid? tenantId, Guid userId)
        => IdentityUserCacheContracts.BuildProfileLogicalKey(tenantId, userId);

    public static string Authz(Guid? tenantId, Guid userId)
        => IdentityUserCacheContracts.BuildAuthorizationLogicalKey(tenantId, userId);

    public static string Alias(Guid? tenantId, string loginInput)
        => IdentityUserCacheContracts.BuildAliasLogicalKey(tenantId, loginInput);

    public static string AliasByHash(Guid? tenantId, string aliasHash)
        => IdentityUserCacheContracts.BuildAliasLogicalKeyByHash(tenantId, aliasHash);

    public static string Lock(Guid? tenantId, Guid userId, string segment)
        => IdentityUserCacheContracts.BuildLockLogicalKey(tenantId, userId, segment);

    public static string NormalizeAlias(string alias)
        => IdentityUserCacheContracts.NormalizeAlias(alias);

    public static string ComputeSha256Hex(string input)
        => IdentityUserCacheContracts.ComputeAliasHash(input);
}
