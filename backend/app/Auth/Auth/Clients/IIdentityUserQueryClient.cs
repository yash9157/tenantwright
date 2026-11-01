using Identity.Application.Contracts.Account;
using Identity.Domain.Shared.Account;

namespace Auth.Clients;

public interface IIdentityUserQueryClient
{
    Task<UserInfoDto?> GetUserInfoAsync(
        string loginInput,
        Guid? tenantId = null,
        InteractiveLoginMode mode = InteractiveLoginMode.Tenant,
        CancellationToken cancellationToken = default);

    Task<UserInfoDto?> GetUserInfoByIdAsync(
        Guid userId,
        Guid? tenantId = null,
        bool strictAuthorization = false,
        bool bypassCache = false,
        CancellationToken cancellationToken = default);
}
