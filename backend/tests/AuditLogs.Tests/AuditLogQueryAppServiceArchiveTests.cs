using AuditLogs.Application.Auditing;
using AuditLogs.Tests.TestDoubles;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using Microsoft.Extensions.Options;
using Moq;
using Shared.BackgroundTasks.Jobs;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain.Repositories;
using Shared.MultiTenancy;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogQueryAppServiceArchiveTests
{
    [Fact]
    public async Task RequestArchiveRestoreAsync_should_be_host_only()
    {
        var service = CreateService(
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            tenantId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.RequestArchiveRestoreAsync(7, new RequestAuditArchiveRestoreInput()));
    }

    [Fact]
    public async Task RequestArchiveRestoreAsync_should_queue_restore_for_host_user()
    {
        var manifest = new AuditArchiveManifest(
            id: 7,
            partitionKey: "tenant:host:202401",
            archivedAtUtc: DateTime.UtcNow.AddDays(-30),
            deleteAfterUtc: DateTime.UtcNow.AddYears(9));
        manifest.MarkArchived(
            archiveUri: "blob://auditlogs-archive/audit-logs/archive/v1/tenant-host/year=2024/month=01/partition-tenant:host:202401.jsonl.gz",
            rowCount: 11,
            checksum: "ABC123",
            archivedAtUtc: DateTime.UtcNow.AddDays(-30),
            deleteAfterUtc: DateTime.UtcNow.AddYears(9));

        var archiveRepo = new Mock<IRepository<AuditArchiveManifest, long>>();
        archiveRepo
            .Setup(x => x.FindAsync(manifest.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifest);
        archiveRepo
            .Setup(x => x.UpdateAsync(manifest, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifest);

        var service = CreateService(
            archiveManifestRepository: archiveRepo.Object,
            tenantId: null,
            backgroundJobs: out var backgroundJobs);

        var result = await service.RequestArchiveRestoreAsync(manifest.Id, new RequestAuditArchiveRestoreInput
        {
            Reason = "verify historical incident"
        });

        Assert.Equal(manifest.Id, result.ManifestId);
        Assert.Equal("queued", result.Status);
        var enqueued = Assert.Single(backgroundJobs.Jobs);
        Assert.Equal(typeof(AuditLogArchiveMaintenanceJobArgs), enqueued.ArgsType);
        Assert.Equal("auditlogs-default", enqueued.Options?.Queue);
    }

    private static AuditLogQueryAppService CreateService(
        IRepository<AuditArchiveManifest, long> archiveManifestRepository,
        Guid? tenantId)
    {
        return CreateService(archiveManifestRepository, tenantId, out _);
    }

    private static AuditLogQueryAppService CreateService(
        IRepository<AuditArchiveManifest, long> archiveManifestRepository,
        Guid? tenantId,
        out CapturingBackgroundJobManager backgroundJobs)
    {
        backgroundJobs = new CapturingBackgroundJobManager();
        var restoreJobRepository = new Mock<IRepository<AuditArchiveRestoreJob, long>>();
        restoreJobRepository
            .Setup(x => x.GetListAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditArchiveRestoreJob, bool>>>(),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        restoreJobRepository
            .Setup(x => x.InsertAsync(It.IsAny<AuditArchiveRestoreJob>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditArchiveRestoreJob job, bool _, CancellationToken _) => job);
        return new AuditLogQueryAppService(
            queryStore: new Mock<AuditLogQueryStore>(null!).Object,
            archiveManifestRepository: archiveManifestRepository,
            archiveRestoreJobRepository: restoreJobRepository.Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: new Mock<IRepository<AuditLogExport, long>>().Object,
            currentTenant: new TestCurrentTenant(tenantId),
            currentUser: new TestCurrentUser(),
            dataFilter: new TestDataFilter(),
            securityOptions: Options.Create(new AuditLogSecurityOptions()),
            archiveOptions: Options.Create(new AuditLogArchiveOptions
            {
                Enabled = true,
                AllowRestore = true
            }),
            backgroundJobManager: backgroundJobs,
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());
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
        public bool IsAuthenticated => true;
        public Guid? Id => Guid.Parse("11111111-1111-1111-1111-111111111111");
        public string? UserName => "host-user";
        public string? Name => "host-user";
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId => null;
        public string? SessionId => null;
        public string[] Roles => [];
        public System.Security.Claims.Claim? FindClaim(string claimType) => null;
        public System.Security.Claims.Claim[] FindClaims(string claimType) => [];
        public System.Security.Claims.Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _dispose;

        public DisposeAction(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
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
