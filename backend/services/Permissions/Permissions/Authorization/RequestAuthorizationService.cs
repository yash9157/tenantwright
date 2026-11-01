using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Permissions.Application.Contracts.Authorization;

namespace Permissions.Authorization;

public sealed class RequestAuthorizationService : IRequestAuthorizationService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuthorizationService _authorizationService;

    public RequestAuthorizationService(IHttpContextAccessor httpContextAccessor, IAuthorizationService authorizationService)
    {
        _httpContextAccessor = httpContextAccessor;
        _authorizationService = authorizationService;
    }

    public async Task EnsureAuthorizedAsync(string policyName, CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            throw new UnauthorizedAccessException("Authorization context is unavailable.");
        }

        var principal = httpContext.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("Authentication is required.");
        }

        var result = await _authorizationService.AuthorizeAsync(principal, policyName);
        if (!result.Succeeded)
        {
            throw new UnauthorizedAccessException("Forbidden.");
        }
    }
}
