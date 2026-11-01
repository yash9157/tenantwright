using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using Identity.Application.Contracts.Account.Risk;

namespace Identity.Application.Account.Risk;

public sealed class HeaderLoginGeoResolver : ILoginGeoResolver, ITransientDependency
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<LoginRiskOptions> _options;

    public HeaderLoginGeoResolver(
        IHttpContextAccessor httpContextAccessor,
        IOptions<LoginRiskOptions> options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    public string? ResolveCountryCode()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return null;
        }

        var headerName = _options.Value.HeaderCountryCodeName;
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return null;
        }

        if (!httpContext.Request.Headers.TryGetValue(headerName, out var rawValue))
        {
            return null;
        }

        var countryCode = rawValue.ToString().Trim();
        if (countryCode.Length < 2 || countryCode.Length > 8)
        {
            return null;
        }

        return countryCode.ToUpperInvariant();
    }
}
