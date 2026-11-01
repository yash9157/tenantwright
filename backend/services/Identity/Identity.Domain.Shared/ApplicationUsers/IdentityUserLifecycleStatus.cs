namespace Identity.Domain.Shared.ApplicationUsers;

public enum IdentityUserLifecycleStatus
{
    PendingInvitation = 0,
    Active = 1,
    Suspended = 2,
    Terminated = 3,
    Archived = 4
}
