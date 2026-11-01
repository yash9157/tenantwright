using System;
using Identity.Domain.Shared.ApplicationUsers;
using Shared.Domain;
using Shared.AuditLogs.Auditing;

namespace Identity.Domain.ApplicationUsers;

public class IdentityClaimType : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = default!;
    public bool Required { get; set; }
    public bool IsStatic { get; set; }
    public string? Regex { get; set; }
    public string? RegexDescription { get; set; }
    public string? Description { get; set; }
    public IdentityClaimValueType ValueType { get; set; }
}
