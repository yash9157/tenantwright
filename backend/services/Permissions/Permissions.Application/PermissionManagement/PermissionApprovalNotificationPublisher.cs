using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.EventContracts;
using Shared.Domain.Repositories;
using Shared.EventBus.Messaging;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.PermissionManagement;

public interface IPermissionApprovalNotificationPublisher
{
    Task RequestedAsync(PermissionGrantChangeRequest request, CancellationToken cancellationToken = default);
    Task DecidedAsync(PermissionGrantChangeRequest request, CancellationToken cancellationToken = default);
}

internal sealed class PermissionApprovalNotificationPublisher
    : IPermissionApprovalNotificationPublisher, ITransientDependency
{
    private readonly IRepository<PermissionGrant, Guid> _grants;
    private readonly IIdentityPermissionTargetValidator _identityTargets;
    private readonly IPermissionApprovalDisplayProjectionResolver _displayProjections;
    private readonly ITenantWrightMessageSender _messages;

    public PermissionApprovalNotificationPublisher(
        IRepository<PermissionGrant, Guid> grants,
        IIdentityPermissionTargetValidator identityTargets,
        IPermissionApprovalDisplayProjectionResolver displayProjections,
        ITenantWrightMessageSender messages)
    {
        _grants = grants;
        _identityTargets = identityTargets;
        _displayProjections = displayProjections;
        _messages = messages;
    }

    public async Task RequestedAsync(PermissionGrantChangeRequest request, CancellationToken cancellationToken = default)
    {
        var queryable = await _grants.GetQueryableAsync();
        var approverGrants = await queryable.AsNoTracking()
            .Where(grant => grant.TenantId == request.TenantId &&
                            grant.Name == PermissionsPolicies.DecideGrantApprovals &&
                            grant.Effect == PermissionGrantEffect.Allow &&
                            (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > DateTime.UtcNow) &&
                            (grant.ProviderName == PermissionProviderNames.User || grant.ProviderName == PermissionProviderNames.Role))
            .Select(grant => new { grant.ProviderName, grant.ProviderKey })
            .ToListAsync(cancellationToken);

        var userIds = approverGrants
            .Where(grant => grant.ProviderName == PermissionProviderNames.User)
            .Select(grant => Guid.TryParseExact(grant.ProviderKey, "D", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var roleIds = approverGrants
            .Where(grant => grant.ProviderName == PermissionProviderNames.Role)
            .Select(grant => Guid.TryParseExact(grant.ProviderKey, "D", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var resolvedRoles = roleIds.Count == 0
            ? []
            : await _identityTargets.ResolveAsync(
                roleIds.Select(id => new IdentityPermissionTargetReference(IdentityPermissionTargetKind.Role, id, request.TenantId)).ToList(),
                cancellationToken);
        var projections = await _displayProjections.ResolveAsync([request], cancellationToken);
        var projection = projections.GetValueOrDefault(request.Id);

        await _messages.PublishEventAsync(new PermissionGrantApprovalRequestedEto
        {
            RequestId = request.Id,
            TenantId = request.TenantId,
            PermissionName = request.PermissionName,
            ProviderDisplayName = projection?.ProviderDisplayName ?? request.ProviderKey,
            RequesterDisplayName = projection?.RequesterDisplayName ?? request.RequestedByUserId?.ToString("D") ?? "System",
            Reason = request.Reason,
            TicketId = request.TicketId,
            ApproverUserIds = userIds,
            ApproverRoleNames = resolvedRoles.Select(role => role.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            OccurredAtUtc = DateTime.UtcNow
        }, cancellationToken);
    }

    public async Task DecidedAsync(PermissionGrantChangeRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.RequestedByUserId.HasValue) return;
        var projections = await _displayProjections.ResolveAsync([request], cancellationToken);
        var projection = projections.GetValueOrDefault(request.Id);
        await _messages.PublishEventAsync(new PermissionGrantApprovalDecidedEto
        {
            RequestId = request.Id,
            TenantId = request.TenantId,
            RequesterUserId = request.RequestedByUserId,
            PermissionName = request.PermissionName,
            ProviderDisplayName = projection?.ProviderDisplayName ?? request.ProviderKey,
            Decision = request.Status.ToString(),
            DecisionReason = request.DecisionReason ?? string.Empty,
            DeciderDisplayName = projection?.ApproverDisplayName ?? request.DecidedByUserId?.ToString("D") ?? "System",
            OccurredAtUtc = request.DecidedAtUtc ?? DateTime.UtcNow
        }, cancellationToken);
    }
}
