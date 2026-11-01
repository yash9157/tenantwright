namespace Identity.Application.Contracts.Caching;

public interface IIdentityAuthCacheInvalidator
{
    Task InvalidateProfileAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task InvalidateAuthorizationAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task InvalidateAliasesAsync(Guid? tenantId, IEnumerable<string> aliasHashes, CancellationToken cancellationToken = default);
}
