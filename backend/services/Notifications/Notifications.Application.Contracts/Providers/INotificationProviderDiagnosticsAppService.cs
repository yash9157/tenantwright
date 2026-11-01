using Notifications.Application.Contracts.Common;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Providers;

public enum NotificationProviderActivitySourceType
{
    Runtime = 1,
    DiagnosticTestSend = 2
}

public enum NotificationProviderHealthStatus
{
    NotConfigured = 1,
    Configured = 2,
    TestSendPassed = 3,
    LiveVerified = 4,
    Attention = 5,
    Inactive = 6
}

public enum NotificationProviderAnalyticsExportFormat
{
    Json = 1,
    Csv = 2
}

public class GetNotificationProviderActivityInput : PagedRequestDto
{
    public Guid? TenantId { get; set; }
    public NotificationStatus? Status { get; set; }
    public NotificationProviderActivitySourceType? SourceType { get; set; }
    public string? TemplateName { get; set; }
    public string? Filter { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
}

public class GetNotificationProviderAnalyticsInput
{
    public Guid? TenantId { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? Provider { get; set; }
    public NotificationChannel? Channel { get; set; }
    public NotificationStatus? Status { get; set; }
    public string? TemplateName { get; set; }
    public string? Filter { get; set; }
}

public class GetCurrentTenantNotificationProviderAnalyticsInput
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public NotificationStatus? Status { get; set; }
    public string? TemplateName { get; set; }
    public string? Filter { get; set; }
}

public class GetNotificationProviderLatencyInput
{
    public Guid? TenantId { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public NotificationChannel? Channel { get; set; }
}

public class GetCurrentTenantNotificationProviderAnalyticsExportInput : GetCurrentTenantNotificationProviderAnalyticsInput
{
    public NotificationProviderAnalyticsExportFormat Format { get; set; } = NotificationProviderAnalyticsExportFormat.Json;
}

public class GetNotificationProviderAnalyticsExportInput : GetNotificationProviderAnalyticsInput
{
    public NotificationProviderAnalyticsExportFormat Format { get; set; } = NotificationProviderAnalyticsExportFormat.Json;
}

public class RecordNotificationProviderLiveVerificationInput
{
    public Guid? TenantId { get; set; }
    public string EvidenceReference { get; set; } = "";
}

public class NotificationProviderLiveVerificationDto
{
    public Guid ConfigurationId { get; set; }
    public Guid? TenantId { get; set; }
    public NotificationChannel Channel { get; set; }
    public NotificationProviderVerificationState VerificationState { get; set; }
    public DateTime VerifiedAtUtc { get; set; }
    public string EvidenceReference { get; set; } = "";
}

public class NotificationProviderSecretRotationStatusDto
{
    public Guid? TenantId { get; set; }
    public bool ProtectionConfigured { get; set; }
    public int TotalSecretCount { get; set; }
    public int CurrentKeySecretCount { get; set; }
    public int RemainingSecretCount { get; set; }
    public bool IsComplete { get; set; }
}

public class NotificationProviderDiagnosticsSummaryDto
{
    public Guid? TenantId { get; set; }
    public int RecentWindowDays { get; set; }
    public List<NotificationProviderChannelHealthDto> Channels { get; set; } = [];
}

public class NotificationProviderChannelHealthDto
{
    public NotificationChannel Channel { get; set; }
    public int ConfigurationCount { get; set; }
    public Guid? DefaultConfigurationId { get; set; }
    public string? DefaultConfigurationName { get; set; }
    public string? ProviderType { get; set; }
    public bool UsesHostFallback { get; set; }
    public bool IsActive { get; set; }
    public DateTime? ConfigurationUpdatedAtUtc { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public string? LastTestResult { get; set; }
    public NotificationProviderVerificationState? VerificationState { get; set; }
    public DateTime? LiveVerifiedAtUtc { get; set; }
    public bool IsLiveVerificationBacked { get; set; }
    public NotificationProviderHealthStatus HealthStatus { get; set; }
    public string HealthMessage { get; set; } = "";
    public DateTime? LatestAttemptAtUtc { get; set; }
    public DateTime? LatestSuccessAtUtc { get; set; }
    public DateTime? LatestFailureAtUtc { get; set; }
    public string? LatestFailureError { get; set; }
    public int RecentAttemptCount { get; set; }
    public int RecentSuccessCount { get; set; }
    public int RecentFailureCount { get; set; }
}

public class NotificationProviderActivityItemDto
{
    public Guid NotificationId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? TenantId { get; set; }
    public NotificationChannel Channel { get; set; }
    public string TemplateName { get; set; } = "";
    public string? Subject { get; set; }
    public NotificationProviderActivitySourceType SourceType { get; set; }
    public NotificationStatus Status { get; set; }
    public DateTime CreationTime { get; set; }
    public int DeliveryCount { get; set; }
    public int QueuedCount { get; set; }
    public int ProcessingCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public DateTime? LatestAttemptAtUtc { get; set; }
    public DateTime? LatestSuccessAtUtc { get; set; }
    public DateTime? LatestFailureAtUtc { get; set; }
    public string? LatestFailureError { get; set; }
}

public class NotificationProviderAnalyticsDto
{
    public Guid? TenantId { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int TotalNotificationCount { get; set; }
    public int TotalDeliveryCount { get; set; }
    public int QueuedCount { get; set; }
    public int ProcessingCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public int RetriedDeliveryCount { get; set; }
    public double SuccessRate { get; set; }
    public double FailureRate { get; set; }
    public double RetryRate { get; set; }
    public int Last24HoursDeliveryCount { get; set; }
    public double Last24HoursSuccessRate { get; set; }
    public int AffectedTemplateCount { get; set; }
    public int AffectedProviderCount { get; set; }
    public List<NotificationProviderAnalyticsStatusMixDto> StatusMix { get; set; } = [];
    public List<NotificationProviderAnalyticsTimeBucketDto> DeliveryVolume { get; set; } = [];
    public List<NotificationProviderAnalyticsChannelVolumeDto> ChannelVolume { get; set; } = [];
    public List<NotificationProviderAnalyticsTopFailureDto> TopFailingTemplates { get; set; } = [];
    public List<NotificationProviderAnalyticsTopFailureDto> TopFailingProviders { get; set; } = [];
    public List<NotificationProviderAnalyticsTopFailureDto> TopErrorCategories { get; set; } = [];
}

public class NotificationProviderAnalyticsStatusMixDto
{
    public NotificationStatus Status { get; set; }
    public int Count { get; set; }
    public double Percentage { get; set; }
}

public class NotificationProviderLatencyReportDto
{
    public Guid? TenantId { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<NotificationProviderLatencyBucketDto> Buckets { get; set; } = [];
}

public class NotificationProviderLatencyBucketDto
{
    public DateTime BucketUtc { get; set; }
    public NotificationChannel Channel { get; set; }
    public long P50Ms { get; set; }
    public long P95Ms { get; set; }
    public long P99Ms { get; set; }
    public int SampleCount { get; set; }
}

public class NotificationProviderAnalyticsTimeBucketDto
{
    public DateTime BucketUtc { get; set; }
    public int TotalCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public int RetriedCount { get; set; }
}

public class NotificationProviderAnalyticsChannelVolumeDto
{
    public NotificationChannel Channel { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public int RetriedCount { get; set; }
    public int TotalCount { get; set; }
    public double ShareOfTotal { get; set; }
}

public class NotificationProviderAnalyticsTopFailureDto
{
    public string Key { get; set; } = "";
    public int FailedCount { get; set; }
    public int TotalCount { get; set; }
    public double FailureRate { get; set; }
}

public class NotificationProviderAnalyticsExportFilterSummaryDto
{
    public Guid? TenantId { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? Provider { get; set; }
    public NotificationChannel? Channel { get; set; }
    public NotificationStatus? Status { get; set; }
    public string? TemplateName { get; set; }
    public string? Filter { get; set; }
}

public class NotificationProviderAnalyticsExportDto
{
    public NotificationProviderAnalyticsExportFormat Format { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Content { get; set; } = "";
    public NotificationProviderAnalyticsExportFilterSummaryDto FilterSummary { get; set; } = new();
}

public readonly record struct NotificationDeliveryLatencyRollupBatchResult(
    int ProcessedBucketCount,
    bool HasMoreUnrolledBuckets);

public interface INotificationDeliveryLatencyRollupProcessor
{
    Task<NotificationDeliveryLatencyRollupBatchResult> RollupBatchAsync(
        CancellationToken cancellationToken = default);
}

public interface INotificationProviderDiagnosticsAppService
{
    Task<NotificationProviderDiagnosticsSummaryDto> GetSummaryAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);
    Task<NotificationProviderSecretRotationStatusDto> GetSecretRotationStatusAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);
    Task<NotificationProviderLiveVerificationDto> RecordLiveVerificationAsync(
        Guid configurationId,
        RecordNotificationProviderLiveVerificationInput input,
        CancellationToken cancellationToken = default);
    Task<PagedResultDto<NotificationProviderActivityItemDto>> GetRecentActivityAsync(
        GetNotificationProviderActivityInput input,
        CancellationToken cancellationToken = default);
    Task<NotificationProviderAnalyticsDto> GetAnalyticsAsync(
        GetNotificationProviderAnalyticsInput input,
        CancellationToken cancellationToken = default);
    Task<NotificationProviderAnalyticsDto> GetCurrentTenantAnalyticsAsync(
        GetCurrentTenantNotificationProviderAnalyticsInput input,
        CancellationToken cancellationToken = default);
    Task<NotificationProviderLatencyReportDto> GetLatencyAsync(
        GetNotificationProviderLatencyInput input,
        CancellationToken cancellationToken = default);
    Task<NotificationProviderAnalyticsExportDto> ExportAnalyticsAsync(
        GetNotificationProviderAnalyticsExportInput input,
        CancellationToken cancellationToken = default);
    Task<NotificationProviderAnalyticsExportDto> ExportCurrentTenantAnalyticsAsync(
        GetCurrentTenantNotificationProviderAnalyticsExportInput input,
        CancellationToken cancellationToken = default);
}
