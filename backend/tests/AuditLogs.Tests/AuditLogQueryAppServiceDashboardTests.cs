using AuditLogs.Application.Auditing;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
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
/// Sub-flow 4: dashboard/summary aggregation endpoint. Uses a real SQLite-backed context so GroupBy
/// aggregations are exercised through actual relational query translation, matching production
/// behavior more closely than the EF Core InMemory provider would.
/// </summary>
public class AuditLogQueryAppServiceDashboardTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AuditLogsDbContextApp _dbContext;

    public AuditLogQueryAppServiceDashboardTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(_connection);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetSummaryReportAsync_should_aggregate_top_users_failed_actions_and_risky_ips()
    {
        var now = DateTime.UtcNow;
        await InsertAsync(userId: "user-1", clientIp: "10.0.0.1", statusCode: 200, executionTime: now.AddHours(-1));
        await InsertAsync(userId: "user-1", clientIp: "10.0.0.1", statusCode: 200, executionTime: now.AddHours(-2));
        await InsertAsync(userId: "user-1", clientIp: "10.0.0.1", statusCode: 500, executionTime: now.AddHours(-3));
        await InsertAsync(userId: "user-2", clientIp: "10.0.0.2", statusCode: 200, executionTime: now.AddHours(-4));
        await InsertAsync(userId: "user-2", clientIp: "10.0.0.1", statusCode: 403, executionTime: now.AddHours(-5));

        var service = CreateService();

        var report = await service.GetSummaryReportAsync(new GetAuditLogSummaryInput
        {
            FromUtc = now.AddDays(-1),
            ToUtc = now,
            TopCount = 10
        });

        Assert.Equal(5, report.TotalEvents);
        Assert.Equal(2, report.FailedEvents);

        Assert.Equal(2, report.TopUsers.Count);
        Assert.Equal("user-1", report.TopUsers[0].UserId);
        Assert.Equal(3, report.TopUsers[0].EventCount);

        Assert.Single(report.FailedActions.Where(x => x.HttpStatusCode == 500));
        Assert.Single(report.FailedActions.Where(x => x.HttpStatusCode == 403));

        var riskyIp = report.RiskyIps.First();
        Assert.Equal("10.0.0.1", riskyIp.ClientIpAddress);
        Assert.Equal(2, riskyIp.FailureCount);
        Assert.Equal(4, riskyIp.TotalCount);
        Assert.Equal(2, riskyIp.DistinctUserCount);
    }

    [Fact]
    public async Task GetSummaryReportAsync_should_include_failed_counts_in_each_hourly_bucket()
    {
        var bucketStartUtc = new DateTime(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc);
        await InsertAsync("user-1", "10.0.0.1", 200, bucketStartUtc.AddMinutes(10));
        await InsertAsync("user-2", "10.0.0.2", 500, bucketStartUtc.AddMinutes(20));
        await InsertAsync("user-3", "10.0.0.3", 404, bucketStartUtc.AddHours(1).AddMinutes(10));

        var report = await CreateService().GetSummaryReportAsync(new GetAuditLogSummaryInput
        {
            FromUtc = bucketStartUtc,
            ToUtc = bucketStartUtc.AddHours(2)
        });

        Assert.Collection(
            report.VolumeOverTime,
            first =>
            {
                Assert.Equal(bucketStartUtc, first.BucketStartUtc);
                Assert.Equal(2, first.EventCount);
                Assert.Equal(1, first.FailedCount);
            },
            second =>
            {
                Assert.Equal(bucketStartUtc.AddHours(1), second.BucketStartUtc);
                Assert.Equal(1, second.EventCount);
                Assert.Equal(1, second.FailedCount);
            });
    }

    [Fact]
    public async Task GetSummaryReportAsync_should_default_to_a_seven_day_window_when_no_range_given()
    {
        var now = DateTime.UtcNow;
        await InsertAsync(userId: "user-1", clientIp: "10.0.0.1", statusCode: 200, executionTime: now.AddDays(-2));
        await InsertAsync(userId: "user-1", clientIp: "10.0.0.1", statusCode: 200, executionTime: now.AddDays(-10));

        var service = CreateService();
        var report = await service.GetSummaryReportAsync(new GetAuditLogSummaryInput());

        Assert.Equal(1, report.TotalEvents);
    }

    [Fact]
    public async Task GetSummaryReportAsync_should_stamp_report_with_utc_generation_time()
    {
        var service = CreateService();
        var beforeRequestUtc = DateTime.UtcNow;

        var report = await service.GetSummaryReportAsync(new GetAuditLogSummaryInput());

        var afterRequestUtc = DateTime.UtcNow;
        Assert.Equal(DateTimeKind.Utc, report.GeneratedAtUtc.Kind);
        Assert.InRange(report.GeneratedAtUtc, beforeRequestUtc, afterRequestUtc);
    }

    [Fact]
    public async Task GetSummaryReportAsync_should_reject_windows_over_the_max_allowed_days()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;

        var exception = await Assert.ThrowsAsync<Shared.SharedValidationException>(() => service.GetSummaryReportAsync(new GetAuditLogSummaryInput
        {
            FromUtc = now.AddDays(-200),
            ToUtc = now
        }));

        Assert.Equal(AuditLogsErrorCodes.DashboardWindowExceeded, exception.Code);
    }

    [Fact]
    public async Task GetSummaryReportAsync_should_apply_the_host_selected_tenant_filter()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var now = DateTime.UtcNow;
        await InsertAsync(userId: "user-1", clientIp: "10.0.0.1", statusCode: 200, executionTime: now.AddHours(-1), tenantId: tenantA);
        await InsertAsync(userId: "user-2", clientIp: "10.0.0.2", statusCode: 200, executionTime: now.AddHours(-1), tenantId: tenantB);

        var service = CreateService();
        var report = await service.GetSummaryReportAsync(new GetAuditLogSummaryInput { TenantId = tenantA });

        Assert.Equal(1, report.TotalEvents);
    }

    [Fact]
    public async Task GetSummaryReportAsync_should_reject_tenant_context_before_data_access()
    {
        var service = CreateService(tenantId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetSummaryReportAsync(new GetAuditLogSummaryInput()));
    }

    private AuditLogQueryAppService CreateService(Guid? tenantId = null)
    {
        return new AuditLogQueryAppService(
            queryStore: new AuditLogQueryStore(new StaticDbContextProvider<AuditLogsDbContextApp>(_dbContext)),
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: new Mock<IRepository<AuditLogExport, long>>().Object,
            currentTenant: new TestCurrentTenant(tenantId),
            currentUser: new TestCurrentUser(),
            dataFilter: new ConfigurableDataFilter(false),
            securityOptions: Options.Create(new AuditLogSecurityOptions()),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: new NoopBackgroundJobManager(),
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());
    }

    private async Task InsertAsync(
        string userId,
        string clientIp,
        int statusCode,
        DateTime executionTime,
        Guid? tenantId = null)
    {
        var auditLog = new AuditLog(
            id: 0,
            eventId: Guid.NewGuid(),
            applicationName: "identity",
            category: "http",
            tenantId: tenantId,
            tenantName: null,
            userId: userId,
            userName: userId,
            executionTime: executionTime,
            executionDuration: 5,
            clientIpAddress: clientIp,
            clientName: null,
            clientId: null,
            correlationId: null,
            traceId: null,
            browserInfo: null,
            httpMethod: "GET",
            url: "/api/test",
            httpStatusCode: statusCode,
            impersonatorUserId: null,
            impersonatorUserName: null,
            impersonatorTenantId: null,
            impersonatorTenantName: null,
            exceptions: null,
            comments: null);

        _dbContext.AuditLogs.Add(auditLog);
        await _dbContext.SaveChangesAsync();
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? tenantId)
        {
            Id = tenantId;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; }
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => new DisposeAction(() => { });
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? Id => Guid.Parse("11111111-1111-1111-1111-111111111111");
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
        public System.Security.Claims.Claim? FindClaim(string claimType) => null;
        public System.Security.Claims.Claim[] FindClaims(string claimType) => [];
        public System.Security.Claims.Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }
}
