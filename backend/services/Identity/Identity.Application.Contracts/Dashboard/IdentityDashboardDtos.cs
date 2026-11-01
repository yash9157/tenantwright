namespace Identity.Application.Contracts.Dashboard;

public class DashboardTrendBucketDto
{
    public DateTime DateUtc { get; set; }
}

public class DashboardLoginOutcomeTrendDto : DashboardTrendBucketDto
{
    public int LoginSuccessCount { get; set; }
    public int LoginFailedCount { get; set; }
    public int LoginInvalidPasswordCount { get; set; }
    public int LoginLockedOutCount { get; set; }
    public int LoginRiskBlockedCount { get; set; }
    public int TwoFactorChallengeCount { get; set; }
}

public class DashboardSessionTrendDto : DashboardTrendBucketDto
{
    public int RevokedCount { get; set; }
}

public class DashboardInvitationTrendDto : DashboardTrendBucketDto
{
    public int CreatedCount { get; set; }
    public int AcceptedCount { get; set; }
    public int CanceledCount { get; set; }
    public int ExpiredCount { get; set; }
}

public class DashboardExpiringInvitationDto
{
    public Guid InvitationId { get; set; }
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? RoleName { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public class DashboardRecentSecurityActionDto
{
    public DateTime OccurredAtUtc { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? SessionId { get; set; }
}

public class DashboardAtRiskUserDto
{
    public Guid UserId { get; set; }

    /// <summary>
    /// Owning tenant of the identity. <see cref="Guid.Empty"/> denotes a platform
    /// staff (host) account. Lets the console deep-link each review-queue row to the
    /// correct person-detail record (staff vs. tenant-user route).
    /// </summary>
    public Guid TenantId { get; set; }

    public string UserName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public int FailedLoginCount { get; set; }
    public int RiskBlockedCount { get; set; }
    public int LockedOutCount { get; set; }
}

/// <summary>
/// Per-tenant MFA posture (C7). A worklist ranked weakest-first so an operator can
/// see who to contact. Tenant display names are owned by the Tenant service, so this
/// Identity-owned contract carries the tenant id; the console resolves the name.
/// </summary>
public class MfaTenantAdoptionDto
{
    public Guid TenantId { get; set; }
    public int EnabledCount { get; set; }
    public int TotalCount { get; set; }
    public decimal AdoptionRate { get; set; }
}

public class MfaAdoptionDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public decimal PlatformAdoptionRate { get; set; }
    public int PlatformEnabledCount { get; set; }
    public int PlatformTotalCount { get; set; }
    public List<MfaTenantAdoptionDto> Tenants { get; set; } = [];
}

public class AdminIdentityDashboardDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public int WindowDays { get; set; }
    public int TotalTenantAdmins { get; set; }
    public int TotalTenantUsers { get; set; }
    public int InactiveUsers { get; set; }
    public int LockedUsers { get; set; }
    public int PendingActivationUsers { get; set; }
    public int ActiveSessions { get; set; }
    public int RevokedSessionsLast24h { get; set; }
    public int PendingInvitations { get; set; }
    public int ExpiringInvitationsNext72h { get; set; }
    public int LoginFailuresLast24h { get; set; }
    public int LoginLockedOutLast24h { get; set; }
    public int RiskBlockedLast24h { get; set; }
    public int TwoFactorChallengesLast24h { get; set; }
    public List<DashboardLoginOutcomeTrendDto> LoginOutcomeTrend { get; set; } = [];
    public List<DashboardSessionTrendDto> SessionRevocationTrend { get; set; } = [];
    public List<DashboardInvitationTrendDto> InvitationLifecycleTrend { get; set; } = [];
    public List<DashboardExpiringInvitationDto> ExpiringInvitations { get; set; } = [];
    public List<DashboardRecentSecurityActionDto> RecentSecurityActions { get; set; } = [];
    public List<DashboardAtRiskUserDto> AtRiskUsers { get; set; } = [];
}

public class TenantIdentityDashboardDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public int WindowDays { get; set; }
    public Guid TenantId { get; set; }
    public int TenantAdminCount { get; set; }
    public int TenantUserCount { get; set; }
    public int InactiveUsers { get; set; }
    public int LockedUsers { get; set; }
    public int PendingActivationUsers { get; set; }
    public int ActiveSessions { get; set; }
    public int RevokedSessionsLast24h { get; set; }
    public int PendingInvitations { get; set; }
    public int ExpiringInvitationsNext72h { get; set; }
    public int LoginFailuresLast24h { get; set; }
    public int LoginLockedOutLast24h { get; set; }
    public int RiskBlockedLast24h { get; set; }
    public int TwoFactorChallengesLast24h { get; set; }
    public List<DashboardLoginOutcomeTrendDto> LoginOutcomeTrend { get; set; } = [];
    public List<DashboardSessionTrendDto> SessionRevocationTrend { get; set; } = [];
    public List<DashboardInvitationTrendDto> InvitationLifecycleTrend { get; set; } = [];
    public List<DashboardExpiringInvitationDto> ExpiringInvitations { get; set; } = [];
    public List<DashboardRecentSecurityActionDto> RecentSecurityActions { get; set; } = [];
    public List<DashboardAtRiskUserDto> AtRiskUsers { get; set; } = [];
}
