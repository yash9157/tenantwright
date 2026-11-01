using Identity.Application.Contracts.Users;

namespace Auth.Clients;

public interface IIdentitySessionClient
{
    Task RevokeSessionAsync(
        Guid userId,
        string sessionId,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateSessionAsync(
        Guid userId,
        string sessionId,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    Task<List<IdentitySessionDto>> GetSessionsAsync(
        Guid userId,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);
}
