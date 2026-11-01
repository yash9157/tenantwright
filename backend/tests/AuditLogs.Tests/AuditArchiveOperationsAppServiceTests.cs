using System.Security.Claims;
using AuditLogs.Application.Auditing;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using AuditLogs.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Shared.BackgroundTasks.Jobs;
using Shared.CurrentUser;
using Shared.Domain.Repositories;
using Xunit;

namespace AuditLogs.Tests;

public sealed class AuditArchiveOperationsAppServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AuditLogsDbContextApp _dbContext;
    private readonly CapturingBackgroundJobManager _backgroundJobs = new();
    private readonly AuditLogQueryAppService _service;

    public AuditArchiveOperationsAppServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(_connection);
        _dbContext.Database.EnsureCreated();
        _service = new AuditLogQueryAppService(
            new Mock<AuditLogQueryStore>(null!).Object,
            new EfTestRepository<AuditArchiveManifest, long>(_dbContext),
            new EfTestRepository<AuditArchiveRestoreJob, long>(_dbContext),
            new EfTestRepository<AuditLogSealVerificationRun, long>(_dbContext),
            new Mock<IRepository<AuditLogExport, long>>().Object,
            new TestCurrentTenant(null),
            new HostCurrentUser(),
            new ConfigurableDataFilter(false),
            Options.Create(new AuditLogSecurityOptions()),
            Options.Create(new AuditLogArchiveOptions
            {
                Enabled = true,
                AllowRestore = true,
                InteractiveSealVerificationMaxRows = 100,
                MaxSealVerificationRows = 1000
            }),
            _backgroundJobs,
            AuditLogTestSignerFactory.CreateDisabled(),
            AuditLogTestSealServiceFactory.CreateDisabled(),
            SharedOnlyTenantDatabases.FanOut());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Restore_request_should_persist_once_and_return_the_in_flight_job_idempotently()
    {
        var manifest = await InsertArchivedManifestAsync();

        var first = await _service.RequestArchiveRestoreAsync(manifest.Id, new RequestAuditArchiveRestoreInput
        {
            Reason = "incident review"
        });
        var second = await _service.RequestArchiveRestoreAsync(manifest.Id, new RequestAuditArchiveRestoreInput
        {
            Reason = "duplicate click"
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("queued", first.Status);
        Assert.Single(await _dbContext.AuditArchiveRestoreJobs.AsNoTracking().ToListAsync());
        Assert.Single(_backgroundJobs.Jobs);
    }

    [Fact]
    public async Task Restore_job_should_support_queued_cancel_and_cancelled_retry_transitions()
    {
        var manifest = await InsertArchivedManifestAsync();
        var requested = await _service.RequestArchiveRestoreAsync(manifest.Id, new RequestAuditArchiveRestoreInput());

        var cancelled = await _service.CancelArchiveRestoreJobAsync(requested.Id);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal(
            AuditArchiveManifestStatus.Archived,
            (await _dbContext.AuditArchiveManifests.FindAsync(manifest.Id))!.Status);

        var retried = await _service.RetryArchiveRestoreJobAsync(requested.Id);
        Assert.Equal("queued", retried.Status);
        Assert.Equal(
            AuditArchiveManifestStatus.RestoreQueued,
            (await _dbContext.AuditArchiveManifests.FindAsync(manifest.Id))!.Status);
        Assert.Equal(2, _backgroundJobs.Jobs.Count);
    }

    [Fact]
    public async Task Large_seal_verification_should_persist_and_queue_then_appear_in_history()
    {
        var queued = await _service.VerifySealRangeAsync(new VerifyAuditLogSealRangeInput
        {
            FromId = 1,
            ToId = 101
        });

        Assert.Equal("queued", queued.Status);
        Assert.Null(queued.ChainIntact);
        var backgroundJob = Assert.Single(_backgroundJobs.Jobs);
        Assert.IsType<AuditLogSealVerificationJobArgs>(backgroundJob.Args);

        var history = await _service.GetSealVerificationsAsync(new GetAuditLogSealVerificationsInput());
        Assert.Equal(1, history.TotalCount);
        Assert.Equal(queued.Id, Assert.Single(history.Items).Id);
    }

    [Fact]
    public async Task Bounded_seal_verification_should_complete_inline_and_persist_its_result()
    {
        var completed = await _service.VerifySealRangeAsync(new VerifyAuditLogSealRangeInput
        {
            FromId = 1,
            ToId = 100
        });

        Assert.Equal("completed", completed.Status);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.NotEmpty(completed.Issues);
        Assert.Empty(_backgroundJobs.Jobs);

        var persisted = await _dbContext.AuditLogSealVerificationRuns
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(AuditLogSealVerificationRunStatus.Completed, persisted.Status);
        Assert.NotNull(persisted.CompletedAtUtc);
    }

    [Fact]
    public async Task Manifest_contract_should_return_an_opaque_label_and_never_the_storage_uri()
    {
        var manifest = await InsertArchivedManifestAsync();

        var result = await _service.GetArchiveManifestsAsync(new GetAuditArchiveManifestsInput());

        var dto = Assert.Single(result.Items);
        Assert.Equal(manifest.PartitionKey, dto.PartitionKey);
        Assert.Null(dto.ArchiveUri);
        Assert.Equal($"Archive partition {manifest.PartitionKey}", dto.LocationLabel);
        Assert.DoesNotContain("blob://", dto.LocationLabel, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AuditArchiveManifest> InsertArchivedManifestAsync()
    {
        var manifest = new AuditArchiveManifest(
            0,
            $"tenant:host:{Guid.NewGuid():N}",
            DateTime.UtcNow.AddDays(-30),
            DateTime.UtcNow.AddYears(9));
        manifest.MarkArchived(
            "blob://auditlogs-archive/audit-logs/archive/v1/tenant-host/year=2024/month=01/archive.jsonl.gz",
            11,
            "ABC123",
            DateTime.UtcNow.AddDays(-30),
            DateTime.UtcNow.AddYears(9));
        _dbContext.AuditArchiveManifests.Add(manifest);
        await _dbContext.SaveChangesAsync();
        return manifest;
    }

    private sealed class HostCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? Id => Guid.Parse("11111111-1111-1111-1111-111111111111");
        public string? UserName => "host-operator";
        public string? Name => "host-operator";
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId => null;
        public string? SessionId => null;
        public string[] Roles => [];
        public Claim? FindClaim(string claimType) => null;
        public Claim[] FindClaims(string claimType) => [];
        public Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }

    private sealed class CapturingBackgroundJobManager : IBackgroundJobManager
    {
        public List<(object Args, BackgroundJobEnqueueOptions? Options)> Jobs { get; } = [];

        public Task<string> EnqueueAsync<TArgs>(
            TArgs args,
            BackgroundJobEnqueueOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Jobs.Add((args!, options));
            return Task.FromResult($"job-{Jobs.Count}");
        }
    }
}
