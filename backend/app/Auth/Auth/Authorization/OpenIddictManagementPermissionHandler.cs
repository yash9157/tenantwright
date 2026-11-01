using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Permissions.Authorization;

namespace Auth.Authorization;

public sealed class OpenIddictManagementPermissionHandler
    : AuthorizationHandler<OpenIddictManagementPermissionRequirement>
{
    private readonly IPermissionChecker _permissionChecker;
    private readonly ILogger<OpenIddictManagementPermissionHandler> _logger;
    private readonly HashSet<string> _allowedInternalClientIds;
    private readonly HashSet<string> _allowedInteractiveClientIds;

    public OpenIddictManagementPermissionHandler(
        IPermissionChecker permissionChecker,
        IConfiguration configuration,
        ILogger<OpenIddictManagementPermissionHandler> logger)
    {
        _permissionChecker = permissionChecker;
        _logger = logger;

        var internalClientId = configuration["OpenIddict:Clients:Internal:ClientId"] ?? "auth-internal";
        var gatewayClientId = configuration["OpenIddict:Clients:TenantWrightGateway:ClientId"] ?? "tenantwright-gateway";
        _allowedInternalClientIds = ParseSet(
            configuration["OpenIddict:Management:AllowedClientIds"] ?? internalClientId);
        _allowedInteractiveClientIds = ParseSet(
            configuration["OpenIddict:Management:AllowedInteractiveClientIds"] ?? gatewayClientId);
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OpenIddictManagementPermissionRequirement requirement)
    {
        if (OpenIddictManagementAuthorization.IsTrustedInternalClient(
                context.User,
                _allowedInternalClientIds))
        {
            context.Succeed(requirement);
            return;
        }

        if (!OpenIddictManagementAuthorization.IsTrustedInteractiveUser(
                context.User,
                _allowedInteractiveClientIds))
        {
            return;
        }

        try
        {
            if (await _permissionChecker.IsGrantedAsync(context.User, requirement.PermissionName))
            {
                context.Succeed(requirement);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "OpenIddict management permission evaluation failed closed for {PermissionName}.",
                requirement.PermissionName);
        }
    }

    private static HashSet<string> ParseSet(string values)
    {
        return values
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
    }
}
