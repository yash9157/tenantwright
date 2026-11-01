using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.Users;

/// <summary>
/// Purpose-limited colleague lookup for pickers — a credentialing reviewer, for example.
///
/// Separate from <see cref="UsersController"/> on purpose. That controller carries a
/// class-level <c>Identity.Users</c> policy, and ASP.NET Core accumulates controller and action
/// authorization rather than replacing it, so an action added there would demand full user
/// management as well as this permission. A caller granted only
/// <c>Identity.Users.Search</c> would be refused, which is the exact over-grant this exists
/// to avoid.
/// </summary>
[ApiController]
[Route("api/identity/user-directory")]
[Authorize(
    AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    Policy = IdentityPermissions.Users.Search)]
public class UserDirectoryController(IUserDirectoryAppService userDirectoryAppService) : ControllerBase
{
    /// <summary>
    /// Searches active users in the caller's own tenant. A query shorter than the minimum
    /// returns an empty result rather than the directory; there is no unfiltered listing and
    /// no paging, so this cannot be walked to enumerate a tenant's users.
    /// </summary>
    [HttpGet("search")]
    public Task<IReadOnlyList<UserDirectoryEntryDto>> SearchAsync(
        [FromQuery] SearchUserDirectoryInput input,
        CancellationToken cancellationToken)
    {
        return userDirectoryAppService.SearchAsync(input, cancellationToken);
    }
}
