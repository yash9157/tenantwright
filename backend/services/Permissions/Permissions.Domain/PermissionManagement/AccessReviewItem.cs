using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.PermissionManagement;

public sealed class AccessReviewItem : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid CampaignId { get; private set; }
    public Guid SourceGrantId { get; private set; }
    public string ProviderName { get; private set; } = default!;
    public string ProviderKey { get; private set; } = default!;
    public string PermissionName { get; private set; } = default!;
    public PermissionGrantEffect Effect { get; private set; }
    public DateTime SourceGrantCreationTimeUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public string? GrantReason { get; private set; }
    public string? TicketId { get; private set; }
    public Guid? GrantedByUserId { get; private set; }
    public AccessReviewItemStatus Status { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecisionReason { get; private set; }
    public long Version { get; private set; }

    private AccessReviewItem()
    {
    }

    public AccessReviewItem(
        Guid id,
        Guid tenantId,
        Guid campaignId,
        Guid sourceGrantId,
        string providerName,
        string providerKey,
        string permissionName,
        PermissionGrantEffect effect,
        DateTime sourceGrantCreationTimeUtc,
        DateTime? expiresAtUtc,
        string? grantReason,
        string? ticketId,
        Guid? grantedByUserId)
        : base(id)
    {
        if (tenantId == Guid.Empty || campaignId == Guid.Empty || sourceGrantId == Guid.Empty)
        {
            throw new ArgumentException("An access review item requires tenant, campaign, and source grant identifiers.");
        }

        TenantId = tenantId;
        CampaignId = campaignId;
        SourceGrantId = sourceGrantId;
        ProviderName = Check.NotNullOrWhiteSpace(providerName, nameof(providerName), PermissionsConsts.MaxProviderNameLength);
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), PermissionsConsts.MaxProviderKeyLength);
        PermissionName = Check.NotNullOrWhiteSpace(permissionName, nameof(permissionName), PermissionsConsts.MaxPermissionNameLength);
        Effect = effect;
        SourceGrantCreationTimeUtc = DateTime.SpecifyKind(sourceGrantCreationTimeUtc, DateTimeKind.Utc);
        ExpiresAtUtc = expiresAtUtc.HasValue ? DateTime.SpecifyKind(expiresAtUtc.Value, DateTimeKind.Utc) : null;
        GrantReason = string.IsNullOrWhiteSpace(grantReason) ? null : Check.NotNullOrWhiteSpace(grantReason, nameof(grantReason), PermissionGrantConsts.MaxReasonLength);
        TicketId = string.IsNullOrWhiteSpace(ticketId) ? null : Check.NotNullOrWhiteSpace(ticketId, nameof(ticketId), PermissionGrantConsts.MaxTicketIdLength);
        GrantedByUserId = grantedByUserId;
        Status = AccessReviewItemStatus.Pending;
        Version = 1;
    }

    public void Certify(Guid reviewerUserId, string decisionReason, DateTime decidedAtUtc)
    {
        Decide(AccessReviewItemStatus.Certified, reviewerUserId, decisionReason, decidedAtUtc);
    }

    public void Revoke(Guid reviewerUserId, string decisionReason, DateTime decidedAtUtc)
    {
        Decide(AccessReviewItemStatus.Revoked, reviewerUserId, decisionReason, decidedAtUtc);
    }

    public void Expire(DateTime expiredAtUtc)
    {
        if (Status != AccessReviewItemStatus.Pending)
        {
            return;
        }

        Status = AccessReviewItemStatus.Expired;
        DecidedAtUtc = DateTime.SpecifyKind(expiredAtUtc, DateTimeKind.Utc);
        Version++;
    }

    private void Decide(AccessReviewItemStatus status, Guid reviewerUserId, string decisionReason, DateTime decidedAtUtc)
    {
        if (Status != AccessReviewItemStatus.Pending)
        {
            throw new InvalidOperationException("Only pending access review items can be decided.");
        }

        if (reviewerUserId == Guid.Empty)
        {
            throw new ArgumentException("A reviewer is required.", nameof(reviewerUserId));
        }

        Status = status;
        DecidedByUserId = reviewerUserId;
        DecidedAtUtc = DateTime.SpecifyKind(decidedAtUtc, DateTimeKind.Utc);
        DecisionReason = Check.NotNullOrWhiteSpace(decisionReason, nameof(decisionReason), AccessReviewConsts.MaxDecisionReasonLength);
        Version++;
    }
}
