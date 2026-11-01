using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.DependencyInjection;

namespace Shared.Security;

[Dependency(ServiceLifetime.Singleton, ReplaceServices = true)]
public class HttpContextCurrentPrincipalAccessor : ThreadCurrentPrincipalAccessor, ISingletonDependency
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentPrincipalAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override ClaimsPrincipal GetClaimsPrincipal()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user != null && (user.Identity?.IsAuthenticated == true || user.Claims.Any()))
        {
            return user;
        }

        return base.GetClaimsPrincipal();
    }
}
