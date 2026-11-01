using Microsoft.AspNetCore.Identity;
using System;
using Shared.Domain;
using Shared.AuditLogs.Auditing;
using Identity.Domain.Shared.ApplicationUsers;

namespace Identity.Domain.ApplicationUsers
{
    public class ApplicationRole : IdentityRole<Guid>, IMultiTenant
    {
        public Guid? TenantId { get; set; }

        // Additive role metadata (P: identity-roles). IsSensitive is an annotation
        // flag only for now (UI labelling); it does not gate assignment/approval.
        public string? Description { get; set; }

        public string? Category { get; set; }

        public bool IsSensitive { get; set; }

        public ApplicationRoleLifecycleState LifecycleState { get; private set; } = ApplicationRoleLifecycleState.Active;

        public long LifecycleVersion { get; private set; }

        public DateTime? RetirementRequestedAtUtc { get; private set; }

        public Guid? RetirementRequestId { get; private set; }

        public ApplicationRole()
        {
        }

        public ApplicationRole(string roleName) : base(roleName)
        {
        }

        public void Rename(string roleName)
        {
            if (LifecycleState != ApplicationRoleLifecycleState.Active)
            {
                throw new InvalidOperationException("A retiring role cannot be renamed.");
            }

            Name = roleName;
            LifecycleVersion++;
        }

        public void BeginRetirement(Guid requestId, DateTime requestedAtUtc)
        {
            if (LifecycleState == ApplicationRoleLifecycleState.Retiring)
            {
                return;
            }

            LifecycleState = ApplicationRoleLifecycleState.Retiring;
            RetirementRequestId = requestId;
            RetirementRequestedAtUtc = DateTime.SpecifyKind(requestedAtUtc, DateTimeKind.Utc);
            LifecycleVersion++;
        }
    }
}
