using System;
using Microsoft.AspNetCore.Identity;
using Shared.Domain;
using Shared.AuditLogs.Auditing;

namespace Identity.Domain.ApplicationUsers;

public class ApplicationUserRole : IdentityUserRole<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }
}
