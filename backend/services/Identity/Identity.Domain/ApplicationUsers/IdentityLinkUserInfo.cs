using System;

namespace Identity.Domain.ApplicationUsers;

public class IdentityLinkUserInfo
{
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }

    public IdentityLinkUserInfo()
    {
    }

    public IdentityLinkUserInfo(Guid userId, Guid? tenantId)
    {
        UserId = userId;
        TenantId = tenantId;
    }
}
