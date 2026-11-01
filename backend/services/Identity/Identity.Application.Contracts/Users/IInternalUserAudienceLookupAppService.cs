namespace Identity.Application.Contracts.Users;

/// <summary>A page of user ids for audience resolution (current-tenant scoped).</summary>
public sealed class UserAudiencePageDto
{
    public List<Guid> UserIds { get; set; } = [];

    public int TotalCount { get; set; }
}

/// <summary>
/// Internal (M2M) lookup used by Notifications to expand group recipient types into concrete users.
/// Tenant scope is taken from the current tenant (set from the internal request's tenant header).
/// </summary>
public interface IInternalUserAudienceLookupAppService
{
    Task<UserAudiencePageDto> GetUsersByRoleAsync(string roleName, int skipCount, int maxResultCount);

    Task<UserAudiencePageDto> GetTenantUserIdsAsync(int skipCount, int maxResultCount);
}
