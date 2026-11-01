using System;
using Shared.Domain;
using Shared.AuditLogs.Auditing;

namespace Identity.Domain.ApplicationUsers;

public class IdentityUserDelegation : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid SourceUserId { get; set; }
    public Guid TargetUserId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}
