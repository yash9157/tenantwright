using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Security;
using System;
using System.Threading.Tasks;
using System.Linq;

namespace Shared.AspNetCore.Host.Middleware;

[ExposeServices(typeof(MultiTenancyMiddleware))]
public class MultiTenancyMiddleware : IMiddleware, ITransientDependency
{
    private const string TenantResolveResultItemKey = "__shared.multiTenancy.resolveResult";

    private readonly ICurrentTenant _currentTenant;
    private readonly ITenantResolver _tenantResolver;
    private readonly ITenantStore _tenantStore;
    private readonly ITenantResolveResultAccessor _tenantResolveResultAccessor;
    private readonly IOptionsMonitor<SharedTenantResolveOptions> _options;
    private readonly ILogger<MultiTenancyMiddleware> _logger;

    public MultiTenancyMiddleware(
        ICurrentTenant currentTenant,
        ITenantResolver tenantResolver,
        ITenantStore tenantStore,
        ITenantResolveResultAccessor tenantResolveResultAccessor,
        IOptionsMonitor<SharedTenantResolveOptions> options,
        ILogger<MultiTenancyMiddleware> logger)
    {
        _currentTenant = currentTenant;
        _tenantResolver = tenantResolver;
        _tenantStore = tenantStore;
        _tenantResolveResultAccessor = tenantResolveResultAccessor;
        _options = options;
        _logger = logger;
    }
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var options = _options.CurrentValue;
        if (IsHostOnlyPath(context.Request.Path.Value, options.HostOnlyPathPrefixes))
        {
            await next(context);
            return;
        }

        var resolveResult = await ResolveTenantAsync(context);
        _tenantResolveResultAccessor.Result = resolveResult;

        try
        {
            switch (resolveResult.Status)
            {
                case TenantResolveStatus.InvalidInput:
                case TenantResolveStatus.AmbiguousInput:
                    _logger.LogWarning(
                        "Rejecting request due to invalid tenant resolution input. Status={Status}, Source={Source}, TenantHash={TenantHash}.",
                        resolveResult.Status,
                        resolveResult.Source ?? "none",
                        TenantResolutionHash.HashTenantId(resolveResult.TenantId));
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("invalid_tenant");
                    return;
                case TenantResolveStatus.TenantMismatch:
                    _logger.LogWarning(
                        "Rejecting request due to tenant mismatch. Source={Source}, TenantHash={TenantHash}.",
                        resolveResult.Source ?? "none",
                        TenantResolutionHash.HashTenantId(resolveResult.TenantId));
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("forbidden_tenant");
                    return;
                case TenantResolveStatus.Unresolved:
                    await next(context);
                    return;
                case TenantResolveStatus.Resolved:
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("invalid_tenant");
                    return;
            }

            if (!resolveResult.TenantId.HasValue || resolveResult.TenantId.Value == Guid.Empty)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("invalid_tenant");
                return;
            }

            var tenant = await _tenantStore.FindAsync(resolveResult.TenantId.Value);
            if (tenant == null || !tenant.IsActive)
            {
                _logger.LogWarning(
                    "Rejecting request due to unavailable tenant. TenantHash={TenantHash}.",
                    TenantResolutionHash.HashTenantId(resolveResult.TenantId));
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("forbidden_tenant");
                return;
            }

            using (_currentTenant.Change(tenant.Id, tenant.Name))
            {
                await next(context);
            }
        }
        finally
        {
            _tenantResolveResultAccessor.Result = null;
        }
    }

    private async Task<TenantResolveResult> ResolveTenantAsync(HttpContext context)
    {
        if (context.Items.TryGetValue(TenantResolveResultItemKey, out var existing) &&
            existing is TenantResolveResult cached)
        {
            return cached;
        }

        var resolveContext = new TenantResolveRequestContext
        {
            IsAuthenticated = context.User?.Identity?.IsAuthenticated == true,
            Claims = context.User?.Claims?.ToArray() ?? [],
            Headers = context.Request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Where(static value => value != null)
                    .Select(static value => value!)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase),
            Path = context.Request.Path.Value
        };

        var resolved = await _tenantResolver.ResolveAsync(resolveContext, context.RequestAborted);
        context.Items[TenantResolveResultItemKey] = resolved;
        return resolved;
    }

    private static bool IsHostOnlyPath(string? path, List<string> configuredPrefixes)
    {
        if (string.IsNullOrWhiteSpace(path) || configuredPrefixes.Count == 0)
        {
            return false;
        }

        return configuredPrefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix) &&
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
