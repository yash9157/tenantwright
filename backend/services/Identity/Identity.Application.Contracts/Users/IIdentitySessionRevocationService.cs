namespace Identity.Application.Contracts.Users;

public interface IIdentitySessionRevocationService
{
    Task<int> RevokeAllAsync(
        Guid userId,
        Guid? tenantId,
        string action,
        string? revocationSource = null,
        Guid? revokedByUserId = null,
        string? revocationReason = null,
        CancellationToken cancellationToken = default);

    Task<int> RevokeAllByTenantAsync(
        Guid tenantId,
        string action,
        string? revocationSource = null,
        Guid? revokedByUserId = null,
        string? revocationReason = null,
        CancellationToken cancellationToken = default);

    Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(
        IReadOnlyCollection<Guid> sessionIds,
        string action,
        string? revocationSource = null,
        Guid? revokedByUserId = null,
        string? revocationReason = null,
        CancellationToken cancellationToken = default);
}
