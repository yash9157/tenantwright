using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.PermissionManagement;

public sealed record PermissionApprovalDisplayProjection(
    string ProviderDisplayName,
    string RequesterDisplayName,
    string? ApproverDisplayName);

public interface IPermissionApprovalDisplayProjectionResolver
{
    Task<IReadOnlyDictionary<Guid, PermissionApprovalDisplayProjection>> ResolveAsync(
        IReadOnlyCollection<PermissionGrantChangeRequest> requests,
        CancellationToken cancellationToken = default);
}

internal sealed class PermissionApprovalDisplayProjectionResolver
    : IPermissionApprovalDisplayProjectionResolver, ITransientDependency
{
    private readonly IIdentityPermissionTargetValidator _identityTargets;

    public PermissionApprovalDisplayProjectionResolver(IIdentityPermissionTargetValidator identityTargets)
    {
        _identityTargets = identityTargets;
    }

    public async Task<IReadOnlyDictionary<Guid, PermissionApprovalDisplayProjection>> ResolveAsync(
        IReadOnlyCollection<PermissionGrantChangeRequest> requests,
        CancellationToken cancellationToken = default)
    {
        var references = new List<IdentityPermissionTargetReference>();
        foreach (var request in requests)
        {
            if (Guid.TryParseExact(request.ProviderKey, "D", out var providerId))
            {
                if (request.ProviderName == PermissionProviderNames.Role)
                    references.Add(new(IdentityPermissionTargetKind.Role, providerId, request.TenantId));
                else if (request.ProviderName == PermissionProviderNames.User)
                    references.Add(new(IdentityPermissionTargetKind.User, providerId, request.TenantId));
            }

            if (request.RequestedByUserId.HasValue)
                references.Add(new(IdentityPermissionTargetKind.User, request.RequestedByUserId.Value, request.TenantId));
            if (request.DecidedByUserId.HasValue)
                references.Add(new(IdentityPermissionTargetKind.User, request.DecidedByUserId.Value, request.TenantId));
        }

        var resolved = references.Count == 0
            ? []
            : await _identityTargets.ResolveAsync(references.Distinct().ToList(), cancellationToken);
        var labels = resolved.ToDictionary(target => (target.Kind, target.Id, target.TenantId), target => target.DisplayName);
        return requests.ToDictionary(
            request => request.Id,
            request => new PermissionApprovalDisplayProjection(
                ResolveProvider(request, labels),
                ResolveUser(request.RequestedByUserId, request.TenantId, labels) ?? "System",
                ResolveUser(request.DecidedByUserId, request.TenantId, labels)));
    }

    private static string ResolveProvider(
        PermissionGrantChangeRequest request,
        IReadOnlyDictionary<(IdentityPermissionTargetKind, Guid, Guid?), string> labels)
    {
        if (!Guid.TryParseExact(request.ProviderKey, "D", out var id)) return request.ProviderKey;
        var kind = request.ProviderName == PermissionProviderNames.Role
            ? IdentityPermissionTargetKind.Role
            : IdentityPermissionTargetKind.User;
        return labels.GetValueOrDefault((kind, id, request.TenantId)) ?? request.ProviderKey;
    }

    private static string? ResolveUser(
        Guid? userId,
        Guid? tenantId,
        IReadOnlyDictionary<(IdentityPermissionTargetKind, Guid, Guid?), string> labels)
        => userId.HasValue
            ? labels.GetValueOrDefault((IdentityPermissionTargetKind.User, userId.Value, tenantId)) ?? userId.Value.ToString("D")
            : null;
}
