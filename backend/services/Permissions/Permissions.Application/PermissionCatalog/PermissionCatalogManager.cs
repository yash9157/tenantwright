using Permissions.Application.Contracts.Definitions;
using Permissions.Domain.Catalog;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.Domain.Repositories;

namespace Permissions.Application.PermissionCatalog;

public class PermissionCatalogManager
{
    private const int MaxParentTraversalDepth = 64;

    private readonly IRepository<PermissionNode, Guid> _repository;

    public PermissionCatalogManager(IRepository<PermissionNode, Guid> repository)
    {
        _repository = repository;
    }

    public async Task<PermissionNode> CreateAsync(
        Guid? tenantId,
        string name,
        PermissionElementType elementType,
        string elementKey,
        string groupName,
        string? displayName = null,
        string? description = null,
        string? parentName = null,
        bool isEnabled = true,
        bool allowsWildcard = true,
        bool requiresApproval = false,
        IEnumerable<string>? requiredFeatures = null,
        IEnumerable<string>? allowedProviders = null,
        IEnumerable<string>? allowedActions = null,
        int sortOrder = 0,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizePermissionName(name);
        var normalizedParentName = string.IsNullOrWhiteSpace(parentName) ? null : NormalizePermissionName(parentName);

        if (normalizedParentName != null)
        {
            await EnsureValidParentChainAsync(tenantId, normalizedName, normalizedParentName, cancellationToken);
        }

        var existing = await _repository.FindAsync(
            x => x.TenantId == tenantId && x.Name == normalizedName,
            includeDetails: false,
            cancellationToken: cancellationToken);
        if (existing != null)
        {
            throw new SharedConflictException(
                PermissionsErrorCodes.PermissionCatalogNameExists,
                $"Permission node already exists: {normalizedName}");
        }

        var node = new PermissionNode(
            id: Guid.CreateVersion7(),
            tenantId: tenantId,
            name: normalizedName,
            elementType: elementType,
            elementKey: elementKey,
            groupName: groupName,
            displayName: displayName,
            description: description,
            parentName: normalizedParentName,
            isEnabled: isEnabled,
            allowsWildcard: allowsWildcard,
            requiresApproval: requiresApproval,
            requiredFeatures: requiredFeatures,
            allowedProviders: allowedProviders,
            allowedActions: allowedActions,
            sortOrder: sortOrder);

        await _repository.InsertAsync(node, autoSave: true, cancellationToken: cancellationToken);
        return node;
    }

    public async Task<PermissionNode> UpdateAsync(
        Guid id,
        Guid? tenantId,
        long expectedVersion,
        string? displayName,
        string? description,
        string groupName,
        string? parentName,
        bool isEnabled,
        bool allowsWildcard,
        bool requiresApproval,
        IEnumerable<string>? requiredFeatures,
        IEnumerable<string>? allowedProviders,
        IEnumerable<string>? allowedActions,
        int sortOrder,
        CancellationToken cancellationToken = default)
    {
        var node = await _repository.GetAsync(x => x.Id == id && x.TenantId == tenantId, includeDetails: false, cancellationToken: cancellationToken);
        if (expectedVersion <= 0 || node.Version != expectedVersion)
        {
            throw new PermissionCatalogConcurrencyException(node.Version);
        }

        var normalizedParentName = string.IsNullOrWhiteSpace(parentName) ? null : NormalizePermissionName(parentName);
        if (normalizedParentName != null)
        {
            await EnsureValidParentChainAsync(tenantId, node.Name, normalizedParentName, cancellationToken);
        }

        node.UpdateMetadata(
            displayName,
            description,
            groupName,
            normalizedParentName,
            isEnabled,
            allowsWildcard,
            requiresApproval,
            requiredFeatures,
            allowedProviders,
            allowedActions,
            sortOrder);

        await _repository.UpdateAsync(node, autoSave: true, cancellationToken: cancellationToken);
        return node;
    }

    private async Task EnsureValidParentChainAsync(Guid? tenantId, string name, string parentName, CancellationToken cancellationToken)
    {
        if (string.Equals(name, parentName, StringComparison.Ordinal))
        {
            throw new SharedValidationException(
                PermissionsErrorCodes.PermissionCatalogParentSelf,
                "ParentName cannot be equal to Name.");
        }

        var parent = await _repository.FindAsync(x => x.TenantId == tenantId && x.Name == parentName, includeDetails: false, cancellationToken: cancellationToken);
        if (parent == null)
        {
            throw new Shared.Domain.Entities.EntityNotFoundException(
                PermissionsErrorCodes.PermissionCatalogParentNotFound,
                $"Parent permission node not found: {parentName}");
        }

        var cursor = parent.ParentName;
        var depth = 0;
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            depth++;
            if (depth > MaxParentTraversalDepth)
            {
                throw new SharedValidationException(
                    PermissionsErrorCodes.PermissionCatalogHierarchyDepthExceeded,
                    "Permission node hierarchy exceeds the maximum depth.");
            }

            if (string.Equals(cursor, name, StringComparison.Ordinal))
            {
                throw new SharedConflictException(
                    PermissionsErrorCodes.PermissionCatalogHierarchyCycle,
                    "Detected cycle in permission node hierarchy.");
            }

            var next = await _repository.FindAsync(x => x.TenantId == tenantId && x.Name == cursor, includeDetails: false, cancellationToken: cancellationToken);
            if (next == null)
            {
                break;
            }

            cursor = next.ParentName;
        }
    }

    private static string NormalizePermissionName(string name)
    {
        return Check.NotNullOrWhiteSpace(name, nameof(name)).Trim();
    }
}

public sealed class PermissionCatalogConcurrencyException : Exception
{
    public long CurrentVersion { get; }

    public PermissionCatalogConcurrencyException(long currentVersion)
        : base("The catalog entry changed after it was loaded.")
    {
        CurrentVersion = currentVersion;
    }
}
