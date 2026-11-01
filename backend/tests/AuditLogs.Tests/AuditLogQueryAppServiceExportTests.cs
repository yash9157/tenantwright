using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuditLogs.Application.Auditing;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.Tests.TestDoubles;
using Microsoft.Extensions.Options;
using Moq;
using Shared.CurrentUser;
using Shared.BackgroundTasks.Jobs;
using Shared.Data.Filtering;
using Shared.Domain.Repositories;
using Shared.MultiTenancy;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogQueryAppServiceExportTests
{
    [Fact]
    public async Task CreateExportAsync_should_queue_job_without_issuing_a_download_token()
    {
        var exportRepository = new Mock<IRepository<AuditLogExport, long>>();
        AuditLogExport? inserted = null;
        exportRepository
            .Setup(x => x.InsertAsync(It.IsAny<AuditLogExport>(), true, It.IsAny<CancellationToken>()))
            .Callback<AuditLogExport, bool, CancellationToken>((entity, _, _) => inserted = entity)
            .ReturnsAsync((AuditLogExport entity, bool _, CancellationToken _) => entity);

        var service = CreateService(
            exportRepository.Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            backgroundJobs: out var backgroundJobs);

        var result = await service.CreateExportAsync(new CreateAuditLogExportInput
        {
            TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Format = "csv",
            MaxRowCount = 100
        });

        Assert.NotNull(inserted);
        Assert.Equal("queued", result.Status);
        Assert.Equal("csv", result.Format);
        Assert.Null(result.DownloadToken);
        Assert.Null(inserted!.DownloadTokenHash);
        Assert.Null(inserted.DownloadTokenExpiresAtUtc);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), inserted.TenantId);
        var enqueued = Assert.Single(backgroundJobs.Jobs);
        Assert.Equal(typeof(AuditLogExportJobArgs), enqueued.ArgsType);
        Assert.Equal(inserted.Id, ((AuditLogExportJobArgs)enqueued.Args).ExportId);
        Assert.Equal(inserted.TenantId, ((AuditLogExportJobArgs)enqueued.Args).TenantId);
        Assert.Equal("auditlogs-default", enqueued.Options?.Queue);
        Assert.Equal(inserted.TenantId, enqueued.Options?.TenantId);
    }

    [Fact]
    public async Task GetExportDownloadAsync_should_reject_invalid_token()
    {
        var export = CreateCompletedExport(token: "valid-token");
        var exportRepository = new Mock<IRepository<AuditLogExport, long>>();
        exportRepository
            .Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);
        exportRepository
            .Setup(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);

        var service = CreateService(
            exportRepository.Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetExportDownloadAsync(export.Id, "wrong-token"));
    }

    [Fact]
    public async Task GetExportDownloadAsync_should_return_decompressed_content_for_valid_token()
    {
        var export = CreateCompletedExport(token: "valid-token");
        var exportRepository = new Mock<IRepository<AuditLogExport, long>>();
        exportRepository
            .Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);

        var service = CreateService(
            exportRepository.Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        var result = await service.GetExportDownloadAsync(export.Id, "valid-token");

        Assert.Equal("audit-logs.csv", result.FileName);
        Assert.Equal("text/csv", result.ContentType);
        Assert.Equal("id,eventId\n1,abc\n", Encoding.UTF8.GetString(result.Content));
        exportRepository.Verify(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetExportDownloadAsync_should_reject_a_reused_token_after_successful_download()
    {
        var export = CreateCompletedExport(token: "valid-token");
        var exportRepository = new Mock<IRepository<AuditLogExport, long>>();
        exportRepository
            .Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);
        exportRepository
            .Setup(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);

        var service = CreateService(
            exportRepository.Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        _ = await service.GetExportDownloadAsync(export.Id, "valid-token");

        Assert.Null(export.DownloadTokenHash);
        Assert.Null(export.DownloadTokenExpiresAtUtc);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetExportDownloadAsync(export.Id, "valid-token"));
    }

    [Fact]
    public async Task GetExportAsync_should_disable_multi_tenant_filter_for_host_requests()
    {
        var export = CreateCompletedExport(token: "valid-token");
        var exportRepository = new Mock<IRepository<AuditLogExport, long>>();
        exportRepository
            .Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);

        var dataFilter = new Mock<IDataFilter>();
        dataFilter
            .Setup(x => x.Disable<Shared.Domain.IMultiTenant>())
            .Returns(new DisposeAction(() => { }));

        var service = CreateService(
            exportRepository.Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            dataFilter: dataFilter.Object);

        _ = await service.GetExportAsync(export.Id);

        dataFilter.Verify(x => x.Disable<Shared.Domain.IMultiTenant>(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetExportAsync_missing_export_should_return_a_coded_not_found_error()
    {
        var exportRepository = new Mock<IRepository<AuditLogExport, long>>();
        exportRepository
            .Setup(x => x.FindAsync(404, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditLogExport?)null);
        var service = CreateService(
            exportRepository.Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        var exception = await Assert.ThrowsAsync<Shared.Domain.Entities.EntityNotFoundException>(() =>
            service.GetExportAsync(404));

        Assert.Equal(AuditLogsErrorCodes.AuditExportNotFound, exception.Code);
        Assert.Equal("Audit export not found.", exception.Message);
    }

    [Fact]
    public async Task CreateExportAsync_invalid_format_should_return_a_coded_validation_error()
    {
        var service = CreateService(
            new Mock<IRepository<AuditLogExport, long>>().Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        var exception = await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            service.CreateExportAsync(new CreateAuditLogExportInput { Format = "xlsx" }));

        Assert.Equal(AuditLogsErrorCodes.InvalidExportFormat, exception.Code);
        Assert.Equal("Export format must be either 'csv' or 'jsonl'.", exception.Message);
    }

    [Fact]
    public async Task IssueExportDownloadTokenAsync_expired_export_should_return_a_coded_conflict_error()
    {
        var export = CreateCompletedExport("valid-token", DateTime.UtcNow.AddMinutes(-1));
        var exportRepository = new Mock<IRepository<AuditLogExport, long>>();
        exportRepository
            .Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);
        exportRepository
            .Setup(x => x.UpdateAsync(export, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);
        var service = CreateService(
            exportRepository.Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        var exception = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            service.IssueExportDownloadTokenAsync(export.Id));

        Assert.Equal(AuditLogsErrorCodes.ExportExpired, exception.Code);
        Assert.Equal("Audit export has expired.", exception.Message);
    }

    [Fact]
    public async Task IssueExportDownloadTokenAsync_non_completed_export_should_return_a_coded_conflict_error()
    {
        var now = DateTime.UtcNow;
        var export = new AuditLogExport(
            id: 44,
            tenantId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            requestedByUserId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            format: AuditLogExportFormat.Csv,
            filterSnapshotJson: "{\"maxRowCount\":100}",
            maxRowCount: 100,
            chunkSize: 50,
            requestedAtUtc: now.AddMinutes(-5),
            expireAtUtc: now.AddHours(1));
        export.MarkProcessing(now.AddMinutes(-4));

        var exportRepository = new Mock<IRepository<AuditLogExport, long>>();
        exportRepository
            .Setup(x => x.FindAsync(export.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);
        var service = CreateService(
            exportRepository.Object,
            tenantId: null,
            userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        var exception = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            service.IssueExportDownloadTokenAsync(export.Id));

        Assert.Equal(AuditLogsErrorCodes.ExportNotReady, exception.Code);
        Assert.Equal("Audit export is not ready for download.", exception.Message);
    }

    private static AuditLogQueryAppService CreateService(
        IRepository<AuditLogExport, long> exportRepository,
        Guid? tenantId,
        Guid? userId,
        IDataFilter? dataFilter = null)
    {
        return CreateService(exportRepository, tenantId, userId, out _, dataFilter);
    }

    private static AuditLogQueryAppService CreateService(
        IRepository<AuditLogExport, long> exportRepository,
        Guid? tenantId,
        Guid? userId,
        out CapturingBackgroundJobManager backgroundJobs,
        IDataFilter? dataFilter = null)
    {
        backgroundJobs = new CapturingBackgroundJobManager();
        return new AuditLogQueryAppService(
            queryStore: new Mock<AuditLogQueryStore>(null!).Object,
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: exportRepository,
            currentTenant: new TestCurrentTenant(tenantId),
            currentUser: new TestCurrentUser(userId),
            dataFilter: dataFilter ?? new TestDataFilter(),
            securityOptions: Options.Create(new AuditLogSecurityOptions()),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: backgroundJobs,
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());
    }

    private static AuditLogExport CreateCompletedExport(string token, DateTime? expireAtUtc = null)
    {
        var rawContent = Encoding.UTF8.GetBytes("id,eventId\n1,abc\n");
        var compressed = AuditLogExportProcessor.Compress(rawContent);
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var now = DateTime.UtcNow;
        var exportExpiresAtUtc = expireAtUtc ?? now.AddHours(1);

        var export = new AuditLogExport(
            id: 42,
            tenantId: tenantId,
            requestedByUserId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            format: AuditLogExportFormat.Csv,
            filterSnapshotJson: "{\"maxRowCount\":100}",
            maxRowCount: 100,
            chunkSize: 50,
            requestedAtUtc: now.AddMinutes(-10),
            expireAtUtc: exportExpiresAtUtc);

        export.ReplaceDownloadToken(ComputeHash(token), now.AddHours(1));

        export.MarkProcessing(now.AddMinutes(-5));
        export.MarkCompleted(
            completedAtUtc: now.AddMinutes(-1),
            fileName: "audit-logs.csv",
            contentType: "text/csv",
            fileContent: compressed,
            compressed: true,
            fileSizeBytes: rawContent.LongLength,
            rowCount: 1);

        return export;
    }

    private static string ComputeHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? tenantId)
        {
            Id = tenantId;
        }

        public bool IsAvailable => Id.HasValue;

        public Guid? Id { get; private set; }

        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new DisposeAction(() => Id = previous);
        }
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public TestCurrentUser(Guid? id)
        {
            Id = id;
        }

        public bool IsAuthenticated => Id.HasValue;

        public Guid? Id { get; }

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

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _dispose;
        private bool _disposed;

        public DisposeAction(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dispose();
        }
    }

    private sealed class TestDataFilter : IDataFilter
    {
        public IDisposable Enable<TFilter>()
            where TFilter : class
            => new DisposeAction(() => { });

        public IDisposable Disable<TFilter>()
            where TFilter : class
            => new DisposeAction(() => { });

        public bool IsEnabled<TFilter>()
            where TFilter : class
            => true;
    }

    private sealed class CapturingBackgroundJobManager : IBackgroundJobManager
    {
        public List<CapturedBackgroundJob> Jobs { get; } = [];

        public Task<string> EnqueueAsync<TArgs>(
            TArgs args,
            BackgroundJobEnqueueOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Jobs.Add(new CapturedBackgroundJob(typeof(TArgs), args!, options));
            return Task.FromResult($"job-{Jobs.Count}");
        }
    }

    private sealed record CapturedBackgroundJob(
        Type ArgsType,
        object Args,
        BackgroundJobEnqueueOptions? Options);
}
