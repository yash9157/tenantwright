namespace Shared.DistributedLocking.Policies;

public static class DistributedLockPolicyNames
{
    public const string EfCoreMigration = "EfCoreMigration";
    public const string EfCoreOutbox = "EfCoreOutbox";
    public const string EventBusOutbox = "EventBusOutbox";
    public const string EventBusInbox = "EventBusInbox";
    public const string AuthIdentitySnapshotRefresh = "AuthIdentitySnapshotRefresh";
    public const string M2mTokenAcquisition = "M2mTokenAcquisition";
    public const string ApiIdempotency = "ApiIdempotency";
    public const string TenantListCacheMutation = "TenantListCacheMutation";
}
