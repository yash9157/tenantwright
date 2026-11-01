using Identity.Domain.Shared.Account.Risk;

namespace Identity.Application.Contracts.SecurityAnalytics;

public sealed class SecurityAnalyticsSummaryDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public int WindowDays { get; set; }
    public int LoginFailuresLast24h { get; set; }
    public int LoginLockedOutLast24h { get; set; }
    public int RiskBlockedLast24h { get; set; }
    public int TwoFactorChallengesLast24h { get; set; }
    public int FalsePositivesMarkedLast24h { get; set; }
    public int ActiveOverrides { get; set; }
    public int ExpiringOverridesNext72h { get; set; }
}

public sealed class SecurityAnalyticsTrendBucketDto
{
    public DateTime DateUtc { get; set; }
    public int LoginFailedCount { get; set; }
    public int LoginInvalidPasswordCount { get; set; }
    public int LoginLockedOutCount { get; set; }
    public int LoginRiskBlockedCount { get; set; }
    public int TwoFactorChallengeCount { get; set; }
    public int FalsePositiveMarkedCount { get; set; }
    public int OverrideCreatedCount { get; set; }
}

public sealed class SecurityAnalyticsTopUserDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public int RiskBlockedCount { get; set; }
    public int FailureCount { get; set; }
    public int LockedOutCount { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}

public sealed class SecurityAnalyticsTopIpDto
{
    public string ClientIpAddress { get; set; } = string.Empty;
    public int RiskBlockedCount { get; set; }
    public int FailureCount { get; set; }
    public int LockedOutCount { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}

public sealed class SecurityAnalyticsExpiringOverrideDto
{
    public Guid OverrideId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string ModeOverride { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }
    public string? Reason { get; set; }
}

public sealed class LoginRiskScoreBucketDto
{
    public int MinimumScore { get; set; }
    public int MaximumScore { get; set; }
    public int EventCount { get; set; }
}

public sealed class LoginRiskCategoryCountDto
{
    public string Category { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public int DistinctUserCount { get; set; }
}

public sealed class LoginRiskReasonCountDto
{
    public string Reason { get; set; } = string.Empty;
    public int EventCount { get; set; }
}

public sealed class LoginRiskReasonCombinationDto
{
    public int Flags { get; set; }
    public List<string> Reasons { get; set; } = [];
    public int EventCount { get; set; }
    public int DistinctUserCount { get; set; }
}

public sealed class LoginRiskBlastRadiusDto
{
    public int WouldBlockEventCount { get; set; }
    public int WouldForceMfaEventCount { get; set; }
    public int WouldDenyForMissingMfaEventCount { get; set; }
    public int TotalDeniedEventCount { get; set; }
    public int DistinctAffectedUserCount { get; set; }
}

public sealed class LoginRiskEnforcementReadinessDto
{
    public LoginRiskMode Mode { get; set; } = LoginRiskMode.Observe;
    public DateTime? ObserveDataSinceUtc { get; set; }
    public int ObserveDataDays { get; set; }
    public int RequiredObserveDataDays { get; set; } = 7;
    public bool HasSufficientObserveData { get; set; }
    public int EligibleHostAdminRecipientCount { get; set; }
    public int PrivilegedUserCount { get; set; }
    public int PrivilegedMfaEnrolledCount { get; set; }
    public decimal PrivilegedMfaCoverageRate { get; set; }
    public bool GeoSignalTrusted { get; set; }
    public int EffectiveGeoShiftWeight { get; set; }
    public bool CanEnableEnforcement { get; set; }
    public List<string> Blockers { get; set; } = [];
}

public sealed class LoginRiskDistributionDto
{
    public int TotalEvaluatedEventCount { get; set; }
    public int DistinctUserCount { get; set; }
    public int FalsePositiveEventCount { get; set; }
    public decimal FalsePositiveRate { get; set; }
    public int MfaSnapshotUnknownEventCount { get; set; }
    public List<LoginRiskScoreBucketDto> ScoreBuckets { get; set; } = [];
    public List<LoginRiskCategoryCountDto> Actions { get; set; } = [];
    public List<LoginRiskCategoryCountDto> Levels { get; set; } = [];
    public List<LoginRiskCategoryCountDto> Scopes { get; set; } = [];
    public List<LoginRiskCategoryCountDto> MfaEnrollment { get; set; } = [];
    public List<LoginRiskReasonCountDto> Reasons { get; set; } = [];
    public List<LoginRiskReasonCombinationDto> ReasonCombinations { get; set; } = [];
    public LoginRiskBlastRadiusDto BlastRadius { get; set; } = new();
    public LoginRiskEnforcementReadinessDto Readiness { get; set; } = new();
}

/// <summary>
/// One hour-of-day x day-of-week cell of the sign-in timing heatmap (C6). Buckets
/// are computed in the returned <see cref="SecurityTimingHeatmapDto.TimeZone"/> so
/// "off hours" is meaningful; the grid is always the full 168 cells, zero-filled.
/// </summary>
public sealed class SecurityTimingCellDto
{
    public int DayOfWeek { get; set; }
    public int Hour { get; set; }
    public int Count { get; set; }
}

public sealed class SecurityTimingHeatmapDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public int WindowDays { get; set; }
    public string Outcome { get; set; } = "success";
    public string TimeZone { get; set; } = "Asia/Kolkata";
    public List<SecurityTimingCellDto> Cells { get; set; } = [];
}

public sealed class AdminSecurityAnalyticsDto
{
    public SecurityAnalyticsSummaryDto Summary { get; set; } = new();
    public LoginRiskDistributionDto RiskDistribution { get; set; } = new();
    public List<SecurityAnalyticsTrendBucketDto> DailyTrend { get; set; } = [];
    public List<SecurityAnalyticsTopUserDto> TopAtRiskUsers { get; set; } = [];
    public List<SecurityAnalyticsTopIpDto> TopSourceIps { get; set; } = [];
    public List<SecurityAnalyticsExpiringOverrideDto> ExpiringOverrides { get; set; } = [];
}

public sealed class TenantSecurityAnalyticsDto
{
    public Guid TenantId { get; set; }
    public SecurityAnalyticsSummaryDto Summary { get; set; } = new();
    public List<SecurityAnalyticsTrendBucketDto> DailyTrend { get; set; } = [];
    public List<SecurityAnalyticsTopUserDto> TopAtRiskUsers { get; set; } = [];
    public List<SecurityAnalyticsTopIpDto> TopSourceIps { get; set; } = [];
    public List<SecurityAnalyticsExpiringOverrideDto> ExpiringOverrides { get; set; } = [];
}
