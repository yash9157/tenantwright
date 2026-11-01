namespace AuditLogs.Domain.Shared.Auditing;

public static class AuditLogConsts
{
    public const int MaxApplicationNameLength = 96;
    public const int MaxCategoryLength = 64;
    public const int MaxUserIdLength = 128;
    public const int MaxUserNameLength = 256;
    public const int MaxTenantNameLength = 128;
    public const int MaxClientIpAddressLength = 128;
    public const int MaxClientNameLength = 128;
    public const int MaxClientIdLength = 128;
    public const int MaxCorrelationIdLength = 128;
    public const int MaxTraceIdLength = 128;
    public const int MaxBrowserInfoLength = 512;
    public const int MaxCommentsLength = 2048;
    public const int MaxUrlLength = 512;
    public const int MaxHttpMethodLength = 16;
    public const int MaxExceptionPayloadLength = 32768;
    public const int MaxSignatureLength = 512;
    public const int MaxSignatureKeyIdLength = 128;

    public const int MaxServiceNameLength = 256;
    public const int MaxMethodNameLength = 128;
    public const int MaxParametersLength = 8192;
    public const int MaxActionResultLength = 64;

    public const int MaxEntityTypeFullNameLength = 256;
    public const int MaxEntityIdLength = 256;
    public const int MaxEntityChangeTypeLength = 64;

    public const int MaxNewValueLength = 4096;
    public const int MaxOriginalValueLength = 4096;
    public const int MaxPropertyNameLength = 128;
    public const int MaxPropertyTypeFullNameLength = 256;

    public const int MaxExtraPropertyKeyLength = 128;
    public const int MaxExtraPropertyValueLength = 2048;

    public const int MaxExportFilterSnapshotLength = 16384;
    public const int MaxExportDownloadTokenHashLength = 128;
    public const int MaxExportFileNameLength = 256;
    public const int MaxExportContentTypeLength = 128;
    public const int MaxExportFailureReasonLength = 2048;
    public const int MaxExportRowCount = 200000;
    public const int MaxExportChunkSize = 5000;
    public const int MaxExportFileContentBytes = 64 * 1024 * 1024;

    public const int MaxArchivePartitionKeyLength = 64;
    public const int MaxArchiveUriLength = 2048;
    public const int MaxArchiveChecksumLength = 256;
    public const int MaxArchiveStatusLength = 64;
    public const int MaxArchiveFailureReasonLength = 2048;
    public const int MaxArchiveRestoreIdempotencyKeyLength = 64;
    public const int MaxSealVerificationIssuesLength = 32768;
}
