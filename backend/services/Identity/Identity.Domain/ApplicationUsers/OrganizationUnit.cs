using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Shared.Domain;
using Shared.AuditLogs.Auditing;

namespace Identity.Domain.ApplicationUsers;

public class OrganizationUnit : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Code { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public Guid? TenantId { get; set; }

    public virtual ICollection<OrganizationUnitRole> Roles { get; set; }
    public virtual ICollection<UserOrganizationUnit> Users { get; set; }

    public OrganizationUnit()
    {
        Roles = new Collection<OrganizationUnitRole>();
        Users = new Collection<UserOrganizationUnit>();
    }
}
public class OrganizationUnitRole : IMultiTenant
{
    public Guid OrganizationUnitId { get; set; }
    public Guid RoleId { get; set; }
    public Guid? TenantId { get; set; }
}
public class UserOrganizationUnit : IMultiTenant
{
    public Guid OrganizationUnitId { get; set; }
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
}
