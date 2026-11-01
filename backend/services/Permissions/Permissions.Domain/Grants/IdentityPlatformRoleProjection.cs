using Shared.Domain.Entities;

namespace Permissions.Domain.Grants;

public sealed class IdentityPlatformRoleProjection : Entity<Guid>
{
    public long LifecycleVersion { get; private set; }
    public bool IsRetiring { get; private set; }
    public Guid? RetirementRequestId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private IdentityPlatformRoleProjection() { }

    public IdentityPlatformRoleProjection(Guid roleId, long lifecycleVersion, DateTime updatedAtUtc)
        : base(roleId)
    {
        LifecycleVersion = lifecycleVersion;
        UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc);
    }

    public bool BeginRetirement(Guid requestId, long lifecycleVersion, DateTime updatedAtUtc)
    {
        if (lifecycleVersion <= LifecycleVersion)
        {
            return false;
        }

        LifecycleVersion = lifecycleVersion;
        RetirementRequestId = requestId;
        IsRetiring = true;
        UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc);
        return true;
    }
}
