using Microsoft.EntityFrameworkCore;
using Permissions.Application.Definitions;
using Permissions.Application.Grants;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.PermissionManagement;

public interface IPendingPermissionGrantValidator
{
    Task ValidateAsync(PermissionGrantChangeRequest request, CancellationToken cancellationToken = default);
}

internal sealed class PendingPermissionGrantValidator : IPendingPermissionGrantValidator, ITransientDependency
{
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly IIdentityPermissionTargetValidator _identityTargets;
    private readonly PermissionDefinitionIndex _definitions;
    private readonly PermissionConflictRuleService _conflictRules;

    public PendingPermissionGrantValidator(
        IRepository<PermissionGrant, Guid> grantRepository,
        IIdentityPermissionTargetValidator identityTargets,
        PermissionDefinitionIndex definitions,
        PermissionConflictRuleService conflictRules)
    {
        _grantRepository = grantRepository;
        _identityTargets = identityTargets;
        _definitions = definitions;
        _conflictRules = conflictRules;
    }

    public async Task ValidateAsync(
        PermissionGrantChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ExpiresAtUtc.HasValue && request.ExpiresAtUtc <= DateTime.UtcNow)
        {
            throw new SharedValidationException("The pending grant has expired and cannot be approved.");
        }

        if (!_definitions.TryGet(request.PermissionName, out var definition) || !definition.IsEnabled)
        {
            throw new SharedValidationException("The pending permission is unknown or disabled.");
        }

        if (request.ProviderName == PermissionProviderNames.Role)
        {
            if (!Guid.TryParseExact(request.ProviderKey, "D", out var roleId) || roleId == Guid.Empty)
            {
                throw new SharedValidationException("The pending role target is invalid.");
            }

            var role = await _identityTargets.GetRoleAsync(roleId, request.TenantId, cancellationToken);
            if (role.IsStatic || !role.IsActive)
            {
                throw new SharedValidationException("The pending role is not an active assignable role.");
            }
        }
        else if (request.ProviderName == PermissionProviderNames.User &&
                 Guid.TryParseExact(request.ProviderKey, "D", out var userId))
        {
            await _identityTargets.GetUserAsync(userId, request.TenantId, cancellationToken);
        }

        var queryable = await _grantRepository.GetQueryableAsync();
        var heldAllows = await queryable
            .Where(x => x.TenantId == request.TenantId &&
                        x.ProviderName == request.ProviderName &&
                        x.ProviderKey == request.ProviderKey &&
                        x.Effect == PermissionGrantEffect.Allow &&
                        (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow))
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);
        var conflictMap = await _conflictRules.BuildConflictMapAsync(request.TenantId, cancellationToken);
        var conflict = PermissionConflictRuleService.FindConflict(
            conflictMap,
            request.PermissionName,
            heldAllows);
        if (conflict != null)
        {
            throw new SharedValidationException(
                $"The pending permission conflicts with an existing permission: {conflict}");
        }
    }
}
