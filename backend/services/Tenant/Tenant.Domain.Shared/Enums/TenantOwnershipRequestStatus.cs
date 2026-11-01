namespace Tenant.Domain.Shared.Enums;

public enum TenantOwnershipRequestStatus
{
    PendingInvitation = 0,
    AwaitingEmailVerification = 1,
    Completed = 2,
    Canceled = 3,
    Expired = 4,
    Failed = 5
}
