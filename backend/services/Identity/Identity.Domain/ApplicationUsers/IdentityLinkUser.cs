using System;
using Shared.AuditLogs.Auditing;

namespace Identity.Domain.ApplicationUsers;

public class IdentityLinkUser
{
    public Guid Id { get; set; }
    public Guid SourceUserId { get; set; }
    public Guid? SourceTenantId { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid? TargetTenantId { get; set; }
}
