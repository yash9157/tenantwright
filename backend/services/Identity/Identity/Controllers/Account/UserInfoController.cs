using System.ComponentModel.DataAnnotations;
using Identity.Application.Contracts.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Controllers.Account;

[ApiController]
[Route("api/identity/account/user-info")]
[Authorize(Policy = "IdentitySelfOrInternal")]
public sealed class UserInfoController(IAccountAppService accountAppService) : ControllerBase
{
    [HttpGet("by-id")]
    public async Task<ActionResult<UserInfoDto?>> GetByIdAsync(
        [Required] Guid userId,
        string? segments = null)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest("User id is required.");
        }

        var parsedSegments = UserInfoEtagHelper.ParseSegments(segments);
        var userInfo = await accountAppService.GetUserInfoByIdAsync(userId, parsedSegments);
        if (userInfo == null)
        {
            return NotFound();
        }

        var etag = UserInfoEtagHelper.Build(userInfo, parsedSegments);
        Response.Headers.ETag = etag;

        if (UserInfoEtagHelper.Matches(Request.Headers.IfNoneMatch.ToString(), etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return userInfo;
    }
}
