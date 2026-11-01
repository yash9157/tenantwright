namespace Tenant.Domain.Shared.Enums;

public enum TenantLifecycleStatus
{
    Active = 0,
    Provisioning = 1,
    Suspended = 2,
    ProvisioningFailed = 3,
    Decommissioned = 4
}
