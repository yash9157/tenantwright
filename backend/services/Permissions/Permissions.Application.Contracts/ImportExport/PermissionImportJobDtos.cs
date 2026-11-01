using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.ImportExport;

public class PermissionImportJobDto
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Kind { get; set; } = default!;
    public string Format { get; set; } = default!;
    public string? ProviderName { get; set; }
    public string? ProviderKey { get; set; }
    public string? ResourceName { get; set; }
    public string? ResourceKey { get; set; }
    public string ContentHash { get; set; } = default!;
    public PermissionImportJobStatus Status { get; set; }
    public int TotalCount { get; set; }
    public int UpsertCount { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int RemovedCount { get; set; }
    public int UnchangedCount { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the apply was accepted and queued to the background worker.</summary>
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? ApplyTokenExpiresAtUtc { get; set; }
    /// <summary>True while a one-time apply token is outstanding (job validated, token not yet consumed/expired).</summary>
    public bool HasPendingApplyToken { get; set; }
    public long Version { get; set; }
}

public class CreatePermissionImportJobDto
{
    /// <summary>The import payload (CSV content).</summary>
    public string Content { get; set; } = default!;

    /// <summary>What is being imported: <c>Catalog</c>, <c>ProviderGrants</c>, or <c>ResourceGrants</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>Required for provider/resource-grant imports.</summary>
    public string? ProviderName { get; set; }
    public string? ProviderKey { get; set; }

    /// <summary>Required for a <c>ResourceGrants</c> import.</summary>
    public string? ResourceName { get; set; }
    public string? ResourceKey { get; set; }
}

/// <summary>Create result: the persisted job, its validation, and — only when valid — the one-time
/// plaintext apply token (returned exactly once; only its hash is stored server-side).</summary>
public class PermissionImportJobCreatedDto
{
    public PermissionImportJobDto Job { get; set; } = default!;
    public PermissionImportValidationResultDto Validation { get; set; } = default!;
    public string? ApplyToken { get; set; }
}

public class ApplyPermissionImportJobDto
{
    public string ApplyToken { get; set; } = default!;
}

public class PagedPermissionImportJobsDto
{
    public long TotalCount { get; set; }
    public List<PermissionImportJobDto> Items { get; set; } = [];
}

public class PermissionImportJobListInput
{
    public PermissionImportJobStatus? Status { get; set; }
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

/// <summary>Typed apply conflict (HTTP 409): the one-time token was invalid, expired, already used,
/// or the job is no longer in a validated state.</summary>
public sealed class PermissionImportJobConflictDto
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public static class PermissionImportJobConflictCodes
{
    public const string InvalidToken = "import-job-invalid-token";
    public const string TokenExpired = "import-job-token-expired";
    public const string NotValidated = "import-job-not-validated";
    public const string NotFound = "import-job-not-found";
    public const string ValidationChanged = "import-job-validation-changed";

    /// <summary>The job is still applying in the background; wait for it to settle.</summary>
    public const string Applying = "import-job-applying";
}
