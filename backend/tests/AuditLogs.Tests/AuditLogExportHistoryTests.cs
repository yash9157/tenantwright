using System.Security.Claims;
using AuditLogs.Application.Auditing;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;
using Shared.BackgroundTasks.Jobs;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain.Repositories;
using Shared.MultiTenancy;
using Xunit;

namespace AuditLogs.Tests;

/// <summary>
/// Coverage for the export-job history list (paging + filters) and the cancel/retry transitions.
/// </summary>
public class AuditLogExportHistoryTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ---- Paging + filters (real SQLite) ----

    [Fact]
    public async Task GetExportsAsync_applies_the_host_selected_tenant_filter_and_pages_with_total_count()
    {
        using var connection = OpenConnection();
        using var seedContext = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(connection);
        seedContext.Database.EnsureCreated();
        Seed(seedContext, id: 1, TenantA, AuditLogExportStatus.Queued, DateTime.UtcNow.AddMinutes(-3));
        Seed(seedContext, id: 2, TenantA, AuditLogExportStatus.Queued, DateTime.UtcNow.AddMinutes(-2));
        Seed(seedContext, id: 3, TenantA, AuditLogExportStatus.Queued, DateTime.UtcNow.AddMinutes(-1));
        Seed(seedContext, id: 4, TenantB, AuditLogExportStatus.Queued, DateTime.UtcNow);
        await seedContext.SaveChangesAsync();

        using var context = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(connection);
        var service = CreateEfService(context, null);

        var firstPage = await service.GetExportsAsync(new GetAuditLogExportsInput { TenantId = TenantA, MaxResultCount = 2 });
        Assert.Equal(3, firstPage.TotalCount); // only Tenant A's exports
        Assert.Equal(2, firstPage.Items.Count);
        // Newest first.
        Assert.Equal(3, firstPage.Items[0].Id);

        var secondPage = await service.GetExportsAsync(new GetAuditLogExportsInput { TenantId = TenantA, SkipCount = 2, MaxResultCount = 2 });
        Assert.Equal(3, secondPage.TotalCount);
        Assert.Single(secondPage.Items);
    }

    [Fact]
    public async Task GetExportsAsync_filters_by_status()
    {
        using var connection = OpenConnection();
        using var seedContext = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(connection);
        seedContext.Database.EnsureCreated();
        Seed(seedContext, id: 1, TenantA, AuditLogExportStatus.Queued, DateTime.UtcNow.AddMinutes(-2));
        Seed(seedContext, id: 2, TenantA, AuditLogExportStatus.Failed, DateTime.UtcNow.AddMinutes(-1));
        await seedContext.SaveChangesAsync();

        using var context = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(connection);
        var service = CreateEfService(context, null);

        var failed = await service.GetExportsAsync(new GetAuditLogExportsInput { TenantId = TenantA, Status = "failed" });
        Assert.Equal(1, failed.TotalCount);
        Assert.Equal("failed", Assert.Single(failed.Items).Status);
    }

    // ---- Cancel / retry (Moq) ----

    [Fact]
    public async Task CancelExportAsync_cancels_a_queued_export()
    {
        var export = NewExport(id: 10, TenantA); // Queued
        var repo = new Mock<IRepository<AuditLogExport, long>>();
        repo.Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(export);
        repo.Setup(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>())).ReturnsAsync(export);

        var service = CreateMoqService(repo.Object, null, out _);
        var result = await service.CancelExportAsync(export.Id);

        Assert.Equal("cancelled", result.Status);
        repo.Verify(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelExportAsync_rejects_a_non_queued_export()
    {
        var export = NewExport(id: 11, TenantA);
        export.MarkProcessing(DateTime.UtcNow); // now Processing
        var repo = new Mock<IRepository<AuditLogExport, long>>();
        repo.Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(export);

        var service = CreateMoqService(repo.Object, null, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelExportAsync(export.Id));
    }

    [Fact]
    public async Task RetryExportAsync_requeues_a_failed_export_and_enqueues_a_job()
    {
        var export = NewExport(id: 12, TenantA);
        export.MarkProcessing(DateTime.UtcNow.AddMinutes(-1));
        export.MarkFailed(DateTime.UtcNow, "boom");
        var repo = new Mock<IRepository<AuditLogExport, long>>();
        repo.Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(export);
        repo.Setup(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>())).ReturnsAsync(export);

        var service = CreateMoqService(repo.Object, null, out var backgroundJobs);
        var result = await service.RetryExportAsync(export.Id);

        Assert.Equal("queued", result.Status);
        Assert.Null(result.FailureReason);
        var enqueued = Assert.Single(backgroundJobs.Jobs);
        Assert.Equal(typeof(AuditLogExportJobArgs), enqueued.ArgsType);
    }

    [Fact]
    public async Task RetryExportAsync_rejects_a_completed_export()
    {
        var export = NewExport(id: 13, TenantA);
        export.MarkProcessing(DateTime.UtcNow.AddMinutes(-1));
        export.MarkCompleted(DateTime.UtcNow, "f.csv", "text/csv", [1, 2, 3], compressed: false, fileSizeBytes: 3, rowCount: 1);
        var repo = new Mock<IRepository<AuditLogExport, long>>();
        repo.Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(export);

        var service = CreateMoqService(repo.Object, null, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryExportAsync(export.Id));
    }

    [Fact]
    public async Task IssueExportDownloadTokenAsync_rotates_a_short_lived_token_for_the_completed_requester_owned_export()
    {
        var export = NewExport(id: 14, TenantA);
        export.MarkProcessing(DateTime.UtcNow.AddMinutes(-1));
        export.MarkCompleted(
            DateTime.UtcNow,
            "audit.csv",
            "text/csv",
            [1, 2, 3],
            compressed: false,
            fileSizeBytes: 3,
            rowCount: 1);
        var repo = new Mock<IRepository<AuditLogExport, long>>();
        repo.Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(export);
        repo.Setup(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>())).ReturnsAsync(export);
        var service = CreateMoqService(repo.Object, null, out _);

        var token = await service.IssueExportDownloadTokenAsync(export.Id);

        Assert.False(string.IsNullOrWhiteSpace(token.DownloadToken));
        Assert.InRange(token.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(9), export.ExpireAtUtc);
        Assert.NotEqual("HASH", export.DownloadTokenHash);
        repo.Verify(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IssueExportDownloadTokenAsync_rejects_a_different_requester_before_mutating_the_export()
    {
        var export = NewExport(id: 15, TenantA);
        export.MarkProcessing(DateTime.UtcNow.AddMinutes(-1));
        export.MarkCompleted(
            DateTime.UtcNow,
            "audit.csv",
            "text/csv",
            [1, 2, 3],
            compressed: false,
            fileSizeBytes: 3,
            rowCount: 1);
        var repo = new Mock<IRepository<AuditLogExport, long>>();
        repo.Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(export);
        var service = CreateMoqService(
            repo.Object,
            null,
            out _,
            Guid.Parse("22222222-2222-2222-2222-222222222222"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.IssueExportDownloadTokenAsync(export.Id));

        repo.Verify(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateExportAsync_does_not_issue_or_persist_a_download_token()
    {
        using var connection = OpenConnection();
        using var context = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(connection);
        context.Database.EnsureCreated();
        var service = CreateEfService(context, null);

        var result = await service.CreateExportAsync(new CreateAuditLogExportInput
        {
            TenantId = TenantA,
            FromUtc = DateTime.UtcNow.AddDays(-1),
            ToUtc = DateTime.UtcNow,
            Format = "csv",
            MaxRowCount = 100
        });

        var export = Assert.Single(context.Set<AuditLogExport>());
        Assert.Null(result.DownloadToken);
        Assert.Null(export.DownloadTokenHash);
        Assert.Null(export.DownloadTokenExpiresAtUtc);
    }

    [Fact]
    public async Task GetExportDownloadAsync_rejects_a_different_requester_before_consuming_a_token()
    {
        var export = NewExport(id: 16, TenantA);
        export.MarkProcessing(DateTime.UtcNow.AddMinutes(-1));
        export.MarkCompleted(
            DateTime.UtcNow,
            "audit.csv",
            "text/csv",
            [1, 2, 3],
            compressed: false,
            fileSizeBytes: 3,
            rowCount: 1);
        var repo = new Mock<IRepository<AuditLogExport, long>>();
        repo.Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(export);
        var service = CreateMoqService(
            repo.Object,
            null,
            out _,
            Guid.Parse("22222222-2222-2222-2222-222222222222"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetExportDownloadAsync(export.Id, "token-presented-by-a-different-requester"));

        Assert.Null(export.DownloadTokenHash);
        Assert.Null(export.DownloadTokenExpiresAtUtc);
        repo.Verify(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetExportDownloadAsync_allows_the_requester_to_consume_a_minted_token()
    {
        var export = NewExport(id: 17, TenantA);
        export.MarkProcessing(DateTime.UtcNow.AddMinutes(-1));
        export.MarkCompleted(
            DateTime.UtcNow,
            "audit.csv",
            "text/csv",
            [1, 2, 3],
            compressed: false,
            fileSizeBytes: 3,
            rowCount: 1);
        var repo = new Mock<IRepository<AuditLogExport, long>>();
        repo.Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(export);
        repo.Setup(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>())).ReturnsAsync(export);
        var service = CreateMoqService(repo.Object, null, out _);

        var issued = await service.IssueExportDownloadTokenAsync(export.Id);
        var download = await service.GetExportDownloadAsync(export.Id, issued.DownloadToken!);

        Assert.Equal("audit.csv", download.FileName);
        Assert.Equal([1, 2, 3], download.Content);
        Assert.Null(export.DownloadTokenHash);
        Assert.Null(export.DownloadTokenExpiresAtUtc);
        repo.Verify(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ---- helpers ----

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    private static void Seed(Microsoft.EntityFrameworkCore.DbContext ctx, long id, Guid tenantId, AuditLogExportStatus status, DateTime requestedAtUtc)
    {
        var export = NewExport(id, tenantId, requestedAtUtc);
        if (status == AuditLogExportStatus.Failed)
        {
            export.MarkProcessing(requestedAtUtc.AddSeconds(1));
            export.MarkFailed(requestedAtUtc.AddSeconds(2), "seed-failure");
        }

        ctx.Set<AuditLogExport>().Add(export);
    }

    private static AuditLogExport NewExport(long id, Guid tenantId, DateTime? requestedAtUtc = null)
    {
        var now = requestedAtUtc ?? DateTime.UtcNow;
        return new AuditLogExport(
            id: id,
            tenantId: tenantId,
            requestedByUserId: "11111111-1111-1111-1111-111111111111",
            format: AuditLogExportFormat.Csv,
            filterSnapshotJson: "{}",
            maxRowCount: 100,
            chunkSize: 50,
            requestedAtUtc: now,
            expireAtUtc: now.AddHours(24));
    }

    private static AuditLogQueryAppService CreateEfService(Microsoft.EntityFrameworkCore.DbContext context, Guid? tenantId)
    {
        return new AuditLogQueryAppService(
            queryStore: new Mock<AuditLogQueryStore>(null!).Object,
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: new EfTestRepository<AuditLogExport, long>(context),
            currentTenant: new TestExportCurrentTenant(tenantId),
            currentUser: new TestExportCurrentUser(),
            dataFilter: new TestExportDataFilter(),
            securityOptions: Options.Create(new AuditLogSecurityOptions()),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: new CapturingExportJobManager(),
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());
    }

    private static AuditLogQueryAppService CreateMoqService(
        IRepository<AuditLogExport, long> exportRepository,
        Guid? tenantId,
        out CapturingExportJobManager backgroundJobs,
        Guid? currentUserId = null)
    {
        backgroundJobs = new CapturingExportJobManager();
        return new AuditLogQueryAppService(
            queryStore: new Mock<AuditLogQueryStore>(null!).Object,
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: exportRepository,
            currentTenant: new TestExportCurrentTenant(tenantId),
            currentUser: new TestExportCurrentUser(currentUserId),
            dataFilter: new TestExportDataFilter(),
            securityOptions: Options.Create(new AuditLogSecurityOptions()),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: backgroundJobs,
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());
    }

    private sealed class TestExportCurrentTenant : ICurrentTenant
    {
        public TestExportCurrentTenant(Guid? id) => Id = id;
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new ExportDisposeAction(() => Id = previous);
        }
    }

    private sealed class TestExportCurrentUser : ICurrentUser
    {
        private readonly Guid _id;

        public TestExportCurrentUser(Guid? id = null)
        {
            _id = id ?? Guid.Parse("11111111-1111-1111-1111-111111111111");
        }

        public bool IsAuthenticated => true;
        public Guid? Id => _id;
        public string? UserName => "tester";
        public string? Name => "tester";
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

    private sealed class TestExportDataFilter : IDataFilter
    {
        public IDisposable Enable<TFilter>() where TFilter : class => new ExportDisposeAction(() => { });
        public IDisposable Disable<TFilter>() where TFilter : class => new ExportDisposeAction(() => { });
        public bool IsEnabled<TFilter>() where TFilter : class => true;
    }

    private sealed class ExportDisposeAction : IDisposable
    {
        private readonly Action _dispose;
        public ExportDisposeAction(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }

    private sealed class CapturingExportJobManager : IBackgroundJobManager
    {
        public List<CapturedJob> Jobs { get; } = [];

        public Task<string> EnqueueAsync<TArgs>(
            TArgs args,
            BackgroundJobEnqueueOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Jobs.Add(new CapturedJob(typeof(TArgs), args!, options));
            return Task.FromResult($"job-{Jobs.Count}");
        }
    }

    private sealed record CapturedJob(Type ArgsType, object Args, BackgroundJobEnqueueOptions? Options);
}
