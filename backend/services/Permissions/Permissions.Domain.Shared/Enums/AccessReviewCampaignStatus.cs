namespace Permissions.Domain.Shared.Enums;

public enum AccessReviewCampaignStatus
{
    Active = 1,
    Closed = 2
}

public enum AccessReviewItemStatus
{
    Pending = 1,
    Certified = 2,
    Revoked = 3,
    Expired = 4
}

public enum AccessReviewDecision
{
    Certify = 1,
    Revoke = 2
}
