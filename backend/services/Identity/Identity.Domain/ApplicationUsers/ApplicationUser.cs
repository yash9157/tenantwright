using Microsoft.AspNetCore.Identity;
using System;
using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Identity.Domain.Shared.ApplicationUsers;

namespace Identity.Domain.ApplicationUsers
{
    public class ApplicationUser : IdentityUser<Guid>, IMultiTenant
    {
        public Guid TenantId { get; set; }
        Guid? IMultiTenant.TenantId => TenantId;
        public bool IsActive { get; set; } = true;
        public IdentityUserLifecycleStatus LifecycleStatus { get; private set; } = IdentityUserLifecycleStatus.Active;
        public bool ShouldChangePasswordOnNextLogin { get; set; }
        public bool RequireEmailActivationBeforeLogin { get; set; }
        /// <summary>
        /// The explicit tenant-primary-administrator designation. This does not grant a role;
        /// the designated user must remain an active TenantAdmin to be returned as primary.
        /// </summary>
        public bool IsPrimaryAdministrator { get; set; }
        /// <summary>
        /// The instant this user record was created. Legacy rows that predate this field remain
        /// unrecorded rather than being assigned an invented historical timestamp.
        /// </summary>
        public DateTime? CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? PendingEmail { get; set; }
        public DateTime? PendingEmailRequestedAtUtc { get; set; }
        public string? Name { get; set; }
        public string? Surname { get; set; }

        /// <summary>
        /// Server-side per-user secret used to HMAC trusted-device fingerprints.
        /// Never exposed to clients. Null for legacy rows; seeded lazily on first use.
        /// </summary>
        public string? DeviceSecret { get; set; }

        public void InitializeLifecycle(bool isActive)
        {
            LifecycleStatus = isActive
                ? IdentityUserLifecycleStatus.Active
                : IdentityUserLifecycleStatus.Suspended;
            IsActive = isActive;
        }

        public IdentityUserLifecycleStatus GetEffectiveLifecycleStatus()
        {
            // Legacy/in-memory rows may predate the lifecycle column. Fail closed
            // when the compatibility boolean says inactive.
            return LifecycleStatus == IdentityUserLifecycleStatus.Active && !IsActive
                ? IdentityUserLifecycleStatus.Suspended
                : LifecycleStatus;
        }

        public void TransitionLifecycle(IdentityUserLifecycleStatus targetStatus)
        {
            var currentStatus = GetEffectiveLifecycleStatus();
            if (currentStatus == targetStatus || !CanTransition(currentStatus, targetStatus))
            {
                throw new InvalidOperationException(
                    $"Identity lifecycle transition from {currentStatus} to {targetStatus} is not allowed.");
            }

            LifecycleStatus = targetStatus;
            IsActive = targetStatus == IdentityUserLifecycleStatus.Active;
        }

        private static bool CanTransition(
            IdentityUserLifecycleStatus currentStatus,
            IdentityUserLifecycleStatus targetStatus)
        {
            return currentStatus switch
            {
                IdentityUserLifecycleStatus.PendingInvitation => targetStatus is
                    IdentityUserLifecycleStatus.Active or IdentityUserLifecycleStatus.Archived,
                IdentityUserLifecycleStatus.Active => targetStatus is
                    IdentityUserLifecycleStatus.Suspended or
                    IdentityUserLifecycleStatus.Terminated or
                    IdentityUserLifecycleStatus.Archived,
                IdentityUserLifecycleStatus.Suspended => targetStatus is
                    IdentityUserLifecycleStatus.Active or IdentityUserLifecycleStatus.Terminated,
                IdentityUserLifecycleStatus.Terminated => targetStatus is
                    IdentityUserLifecycleStatus.Active or IdentityUserLifecycleStatus.Archived,
                IdentityUserLifecycleStatus.Archived => targetStatus == IdentityUserLifecycleStatus.Active,
                _ => false
            };
        }
    }
}
