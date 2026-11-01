using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.PermissionManagement;

public sealed class AccessReviewCampaign : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public DateTime SnapshotAtUtc { get; private set; }
    public DateTime DueAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public AccessReviewCampaignStatus Status { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public Guid? ClosedByUserId { get; private set; }

    private AccessReviewCampaign()
    {
    }

    public AccessReviewCampaign(
        Guid id,
        Guid tenantId,
        string name,
        DateTime snapshotAtUtc,
        DateTime dueAtUtc,
        Guid createdByUserId)
        : base(id)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant is required for an access review campaign.", nameof(tenantId));
        }

        if (createdByUserId == Guid.Empty)
        {
            throw new ArgumentException("A campaign creator is required.", nameof(createdByUserId));
        }

        SnapshotAtUtc = DateTime.SpecifyKind(snapshotAtUtc, DateTimeKind.Utc);
        DueAtUtc = DateTime.SpecifyKind(dueAtUtc, DateTimeKind.Utc);
        if (DueAtUtc <= SnapshotAtUtc)
        {
            throw new ArgumentException("An access review due date must be after its snapshot time.", nameof(dueAtUtc));
        }

        TenantId = tenantId;
        Name = Check.NotNullOrWhiteSpace(name, nameof(name), AccessReviewConsts.MaxCampaignNameLength);
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = SnapshotAtUtc;
        Status = AccessReviewCampaignStatus.Active;
    }

    public void Close(Guid closedByUserId, DateTime closedAtUtc)
    {
        if (Status != AccessReviewCampaignStatus.Active)
        {
            throw new InvalidOperationException("Only active access review campaigns can be closed.");
        }

        if (closedByUserId == Guid.Empty)
        {
            throw new ArgumentException("A closing reviewer is required.", nameof(closedByUserId));
        }

        Status = AccessReviewCampaignStatus.Closed;
        ClosedByUserId = closedByUserId;
        ClosedAtUtc = DateTime.SpecifyKind(closedAtUtc, DateTimeKind.Utc);
    }
}
