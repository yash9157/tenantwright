using Shared;
using Shared.Domain;
using Shared.Domain.Entities;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.ImportExport;

/// <summary>
/// A durable permission-import operation (BE-8). Created by validating an uploaded payload; the exact
/// validated <see cref="Content"/> is retained as the snapshot so apply cannot act on a different
/// payload than was reviewed. Applying requires a one-time apply token (only a hash + expiry are
/// stored, never the token) bound to this job's requester, tenant scope, and content — consumed on a
/// successful apply so it can never be replayed.
/// </summary>
[Audited]
public class PermissionImportJob : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string Kind { get; private set; } = default!;
    public string Format { get; private set; } = default!;

    /// <summary>Target provider for provider/resource-grant jobs (null for a catalog job).</summary>
    public string? ProviderName { get; private set; }
    public string? ProviderKey { get; private set; }

    /// <summary>Target resource for a <c>ResourceGrants</c> job.</summary>
    public string? ResourceName { get; private set; }
    public string? ResourceKey { get; private set; }

    /// <summary>The exact validated payload snapshot; apply re-validates and applies this, not a re-upload.</summary>
    public string Content { get; private set; } = default!;

    /// <summary>SHA-256 of <see cref="Content"/>, for integrity and display.</summary>
    public string ContentHash { get; private set; } = default!;

    public PermissionImportJobStatus Status { get; private set; }

    public int TotalCount { get; private set; }
    public int UpsertCount { get; private set; }
    public int CreatedCount { get; private set; }
    public int UpdatedCount { get; private set; }
    public int RemovedCount { get; private set; }
    public int UnchangedCount { get; private set; }

    public Guid? RequestedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>When the apply was accepted and queued to the background worker.</summary>
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>SHA-256 of the one-time apply token; null when no token is outstanding or it was consumed.</summary>
    public string? ApplyTokenHash { get; private set; }
    public DateTime? ApplyTokenExpiresAtUtc { get; private set; }

    public long Version { get; private set; } = 1;

    protected PermissionImportJob()
    {
    }

    public PermissionImportJob(
        Guid id,
        Guid? tenantId,
        string kind,
        string format,
        string content,
        string contentHash,
        PermissionImportJobStatus status,
        Guid? requestedByUserId,
        DateTime createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        Kind = Check.NotNullOrWhiteSpace(kind, nameof(kind), maxLength: 64);
        Format = Check.NotNullOrWhiteSpace(format, nameof(format), maxLength: 16);
        Content = content ?? string.Empty;
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash), maxLength: 128);
        Status = status;
        RequestedByUserId = requestedByUserId;
        CreatedAtUtc = createdAtUtc;
        Version = 1;
    }

    /// <summary>Binds a provider-grants job to its target provider. Set once, at creation.</summary>
    public void SetProviderScope(string providerName, string providerKey)
    {
        ProviderName = Check.NotNullOrWhiteSpace(providerName, nameof(providerName), maxLength: 128);
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), maxLength: 256);
    }

    /// <summary>Binds a resource-grants job to its immutable resource and provider scope.</summary>
    public void SetResourceScope(
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey)
    {
        ResourceName = Check.NotNullOrWhiteSpace(resourceName, nameof(resourceName), maxLength: 128);
        ResourceKey = Check.NotNullOrWhiteSpace(resourceKey, nameof(resourceKey), maxLength: 256);
        SetProviderScope(providerName, providerKey);
    }

    public void SetValidation(PermissionImportJobStatus status, int totalCount, int upsertCount)
    {
        Status = status;
        TotalCount = totalCount;
        UpsertCount = upsertCount;
        Version++;
    }

    /// <summary>Stores the hash + expiry of a freshly minted one-time apply token. Only valid while
    /// the job is <see cref="PermissionImportJobStatus.Validated"/>.</summary>
    public void IssueApplyToken(string tokenHash, DateTime expiresAtUtc)
    {
        EnsureStatus(PermissionImportJobStatus.Validated);
        ApplyTokenHash = Check.NotNullOrWhiteSpace(tokenHash, nameof(tokenHash), maxLength: 128);
        ApplyTokenExpiresAtUtc = expiresAtUtc;
        Version++;
    }

    /// <summary>Accepts an apply: consumes the one-time token and hands the job to the background
    /// worker. Consuming the token here (not at completion) is what makes an accepted apply
    /// un-replayable even while the worker is still running.</summary>
    public void MarkApplying(DateTime startedAtUtc)
    {
        EnsureStatus(PermissionImportJobStatus.Validated);
        Status = PermissionImportJobStatus.Applying;
        StartedAtUtc = startedAtUtc;
        ClearApplyToken();
        Version++;
    }

    public void MarkApplied(int created, int updated, int removed, int unchanged, DateTime appliedAtUtc)
    {
        EnsureStatus(PermissionImportJobStatus.Applying);
        Status = PermissionImportJobStatus.Applied;
        CreatedCount = created;
        UpdatedCount = updated;
        RemovedCount = removed;
        UnchangedCount = unchanged;
        CompletedAtUtc = appliedAtUtc;
        ClearApplyToken();
        Version++;
    }

    public void MarkFailed(string reason, DateTime failedAtUtc)
    {
        Status = PermissionImportJobStatus.Failed;
        FailureReason = reason.Length > 512 ? reason[..512] : reason;
        CompletedAtUtc = failedAtUtc;
        ClearApplyToken();
        Version++;
    }

    public void Cancel(DateTime cancelledAtUtc)
    {
        EnsureStatus(PermissionImportJobStatus.Validated);
        Status = PermissionImportJobStatus.Cancelled;
        CompletedAtUtc = cancelledAtUtc;
        ClearApplyToken();
        Version++;
    }

    /// <summary>
    /// True when a job has been sitting in <see cref="PermissionImportJobStatus.Applying"/> longer
    /// than <paramref name="staleAfter"/> — i.e. the worker that owned it died mid-apply. Without
    /// this the job would be stuck in <c>Applying</c> forever, since nothing else may touch it.
    /// </summary>
    public bool IsApplyStalled(DateTime utcNow, TimeSpan staleAfter) =>
        Status == PermissionImportJobStatus.Applying &&
        StartedAtUtc is not null &&
        utcNow - StartedAtUtc.Value > staleAfter;

    /// <summary>Re-opens a non-applied job for a fresh validation pass (retry), clearing any prior token.</summary>
    public void ResetForRetry(DateTime utcNow, TimeSpan applyStaleAfter)
    {
        if (Status == PermissionImportJobStatus.Applied)
        {
            throw new InvalidOperationException("An applied import job cannot be retried.");
        }

        if (Status == PermissionImportJobStatus.Applying && !IsApplyStalled(utcNow, applyStaleAfter))
        {
            throw new InvalidOperationException("An import job that is currently applying cannot be retried.");
        }

        StartedAtUtc = null;
        CompletedAtUtc = null;
        FailureReason = null;
        CreatedCount = UpdatedCount = RemovedCount = UnchangedCount = 0;
        ClearApplyToken();
        Version++;
    }

    private void ClearApplyToken()
    {
        ApplyTokenHash = null;
        ApplyTokenExpiresAtUtc = null;
    }

    private void EnsureStatus(PermissionImportJobStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Import job is {Status}; expected {expected}.");
        }
    }
}
