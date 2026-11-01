namespace Permissions.Application.Contracts.Definitions;

public enum PermissionElementType : byte
{
    Project = 1,
    Module = 2,
    Page = 3,
    Menu = 4,
    Feature = 5,
    Api = 6,
    Action = 7
}

public enum EntitlementMutationPolicy
{
    ProvisioningOnly = 1,
    TenantAllocationAllowed = 2
}
