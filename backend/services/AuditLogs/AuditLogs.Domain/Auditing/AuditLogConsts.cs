namespace AuditLogs.Domain.Auditing;

public static class AuditLogConsts
{
    public const int MaxApplicationNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxApplicationNameLength;
    public const int MaxCategoryLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxCategoryLength;
    public const int MaxUserIdLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxUserIdLength;
    public const int MaxUserNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxUserNameLength;
    public const int MaxTenantNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxTenantNameLength;
    public const int MaxClientIpAddressLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxClientIpAddressLength;
    public const int MaxClientNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxClientNameLength;
    public const int MaxClientIdLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxClientIdLength;
    public const int MaxCorrelationIdLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxCorrelationIdLength;
    public const int MaxTraceIdLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxTraceIdLength;
    public const int MaxBrowserInfoLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxBrowserInfoLength;
    public const int MaxCommentsLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxCommentsLength;
    public const int MaxUrlLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxUrlLength;
    public const int MaxHttpMethodLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxHttpMethodLength;
    public const int MaxExceptionPayloadLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExceptionPayloadLength;
    public const int MaxSignatureLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxSignatureLength;
    public const int MaxSignatureKeyIdLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxSignatureKeyIdLength;

    public const int MaxServiceNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxServiceNameLength;
    public const int MaxMethodNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxMethodNameLength;
    public const int MaxParametersLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxParametersLength;
    public const int MaxActionResultLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxActionResultLength;

    public const int MaxEntityTypeFullNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxEntityTypeFullNameLength;
    public const int MaxEntityIdLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxEntityIdLength;
    public const int MaxEntityChangeTypeLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxEntityChangeTypeLength;

    public const int MaxNewValueLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxNewValueLength;
    public const int MaxOriginalValueLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxOriginalValueLength;
    public const int MaxPropertyNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxPropertyNameLength;
    public const int MaxPropertyTypeFullNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxPropertyTypeFullNameLength;

    public const int MaxExtraPropertyKeyLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExtraPropertyKeyLength;
    public const int MaxExtraPropertyValueLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExtraPropertyValueLength;

    public const int MaxExportFilterSnapshotLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExportFilterSnapshotLength;
    public const int MaxExportDownloadTokenHashLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExportDownloadTokenHashLength;
    public const int MaxExportFileNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExportFileNameLength;
    public const int MaxExportContentTypeLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExportContentTypeLength;
    public const int MaxExportFailureReasonLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExportFailureReasonLength;
    public const int MaxExportRowCount = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExportRowCount;
    public const int MaxExportChunkSize = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExportChunkSize;
    public const int MaxExportFileContentBytes = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxExportFileContentBytes;

    public const int MaxArchivePartitionKeyLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxArchivePartitionKeyLength;
    public const int MaxArchiveUriLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxArchiveUriLength;
    public const int MaxArchiveChecksumLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxArchiveChecksumLength;
    public const int MaxArchiveStatusLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxArchiveStatusLength;
    public const int MaxArchiveFailureReasonLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxArchiveFailureReasonLength;
    public const int MaxArchiveRestoreIdempotencyKeyLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxArchiveRestoreIdempotencyKeyLength;
    public const int MaxSealVerificationIssuesLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogConsts.MaxSealVerificationIssuesLength;
}

public static class AuditLogSealConsts
{
    public const int MaxHashLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogSealConsts.MaxHashLength;
    public const int MaxSignatureLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogSealConsts.MaxSignatureLength;
    public const int MaxSignatureKeyIdLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogSealConsts.MaxSignatureKeyIdLength;
    public const string GenesisHash = global::AuditLogs.Domain.Shared.Auditing.AuditLogSealConsts.GenesisHash;
}

public static class AuditLogLegalHoldConsts
{
    public const int MaxNameLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogLegalHoldConsts.MaxNameLength;
    public const int MaxReasonLength = global::AuditLogs.Domain.Shared.Auditing.AuditLogLegalHoldConsts.MaxReasonLength;
}
