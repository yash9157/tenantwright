using Shared;
using Shared.Domain;
using Shared.Domain.Entities;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.Features;

[Audited]
public class PermissionFeatureBinding : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public string PermissionName { get; private set; } = default!;

    public string FeatureName { get; private set; } = default!;

    public PermissionFeatureRequirement Requirement { get; private set; }

    protected PermissionFeatureBinding()
    {
    }

    public PermissionFeatureBinding(
        Guid id,
        Guid? tenantId,
        string permissionName,
        string featureName,
        PermissionFeatureRequirement requirement)
        : base(id)
    {
        TenantId = tenantId;
        PermissionName = Check.NotNullOrWhiteSpace(permissionName, nameof(permissionName), maxLength: PermissionsConsts.MaxPermissionNameLength);
        FeatureName = Check.NotNullOrWhiteSpace(featureName, nameof(featureName), maxLength: PermissionsConsts.MaxFeatureNameLength);
        Requirement = requirement;
    }

    public void SetRequirement(PermissionFeatureRequirement requirement)
    {
        Requirement = requirement;
    }
}
