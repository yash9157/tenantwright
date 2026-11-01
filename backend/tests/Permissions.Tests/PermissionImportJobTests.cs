using System;
using Permissions.Domain.ImportExport;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Security invariants of the durable import job (BE-8): a one-time apply token can only be issued for
/// a validated job, is cleared when the job reaches a terminal state, and an applied job cannot be
/// re-applied or retried.
/// </summary>
public class PermissionImportJobTests
{
    private static PermissionImportJob ValidatedJob()
    {
        var job = new PermissionImportJob(
            Guid.NewGuid(), tenantId: null, "Catalog", "csv", "name,group\nX,Y", "hash",
            PermissionImportJobStatus.Validated, Guid.NewGuid(), DateTime.UtcNow);
        job.SetValidation(PermissionImportJobStatus.Validated, totalCount: 1, upsertCount: 1);
        return job;
    }

    [Fact]
    public void Apply_token_can_only_be_issued_for_a_validated_job()
    {
        var invalid = new PermissionImportJob(
            Guid.NewGuid(), null, "Catalog", "csv", "bad", "hash",
            PermissionImportJobStatus.Invalid, Guid.NewGuid(), DateTime.UtcNow);

        Assert.Throws<InvalidOperationException>(() => invalid.IssueApplyToken("tokenhash", DateTime.UtcNow.AddMinutes(15)));
    }

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    [Fact]
    public void Accepting_an_apply_consumes_the_token_before_the_worker_runs()
    {
        var job = ValidatedJob();
        job.IssueApplyToken("tokenhash", DateTime.UtcNow.AddMinutes(15));

        job.MarkApplying(DateTime.UtcNow);

        Assert.Equal(PermissionImportJobStatus.Applying, job.Status);
        Assert.NotNull(job.StartedAtUtc);
        // The token is gone the moment the apply is accepted, so it cannot be replayed while the
        // background worker is still running.
        Assert.Null(job.ApplyTokenHash);
        Assert.Null(job.ApplyTokenExpiresAtUtc);
        Assert.Throws<InvalidOperationException>(() => job.MarkApplying(DateTime.UtcNow));
    }

    [Fact]
    public void Only_an_applying_job_can_be_marked_applied()
    {
        var validated = ValidatedJob();
        Assert.Throws<InvalidOperationException>(() => validated.MarkApplied(1, 0, 0, 0, DateTime.UtcNow));
    }

    [Fact]
    public void Applied_is_terminal()
    {
        var job = ValidatedJob();
        job.IssueApplyToken("tokenhash", DateTime.UtcNow.AddMinutes(15));
        job.MarkApplying(DateTime.UtcNow);

        job.MarkApplied(created: 1, updated: 0, removed: 0, unchanged: 0, DateTime.UtcNow);

        Assert.Equal(PermissionImportJobStatus.Applied, job.Status);
        Assert.Null(job.ApplyTokenHash);
        // Cannot apply again.
        Assert.Throws<InvalidOperationException>(() => job.MarkApplied(1, 0, 0, 0, DateTime.UtcNow));
        // Cannot retry an applied job.
        Assert.Throws<InvalidOperationException>(() => job.ResetForRetry(DateTime.UtcNow, StaleAfter));
    }

    [Fact]
    public void An_in_flight_apply_cannot_be_retried_until_it_stalls()
    {
        var startedAt = DateTime.UtcNow;
        var job = ValidatedJob();
        job.IssueApplyToken("tokenhash", startedAt.AddMinutes(15));
        job.MarkApplying(startedAt);

        Assert.False(job.IsApplyStalled(startedAt.AddMinutes(5), StaleAfter));
        Assert.Throws<InvalidOperationException>(() => job.ResetForRetry(startedAt.AddMinutes(5), StaleAfter));

        // A worker that died mid-apply must not strand the job forever.
        var afterStale = startedAt.AddMinutes(31);
        Assert.True(job.IsApplyStalled(afterStale, StaleAfter));
        job.ResetForRetry(afterStale, StaleAfter);
        Assert.Null(job.StartedAtUtc);
    }

    [Fact]
    public void Failure_and_cancel_clear_the_token()
    {
        var failed = ValidatedJob();
        failed.IssueApplyToken("tokenhash", DateTime.UtcNow.AddMinutes(15));
        failed.MarkFailed("catalog changed", DateTime.UtcNow);
        Assert.Null(failed.ApplyTokenHash);

        var cancelled = ValidatedJob();
        cancelled.IssueApplyToken("tokenhash", DateTime.UtcNow.AddMinutes(15));
        cancelled.Cancel(DateTime.UtcNow);
        Assert.Equal(PermissionImportJobStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.ApplyTokenHash);
    }

    [Fact]
    public void Resource_grant_job_persists_the_complete_target_scope()
    {
        var job = ValidatedJob();

        job.SetResourceScope("Document", "document-42", "User", Guid.NewGuid().ToString("D"));

        Assert.Equal("Document", job.ResourceName);
        Assert.Equal("document-42", job.ResourceKey);
        Assert.Equal("User", job.ProviderName);
        Assert.NotNull(job.ProviderKey);
    }
}
