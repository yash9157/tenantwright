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
/// The total count on both cursor-paged reads — the audit list and an entity's change history — is
/// opt-in.
///
/// Counting the filtered set is a scan of the largest table in the system, and it ran on every cursor
/// page whether or not anything used the number. Cursor paging navigates by <c>NextCursor</c>, so the
/// count is only needed by a caller that renders a total — and <c>UrlContains</c> makes the same scan
/// unindexed on top of that.
///
/// The offset-paged reads (exports, legal holds, archive) still count unconditionally: their paginator
/// needs the total to render page numbers, so there the count is load-bearing rather than waste.
///
/// SQLite rather than the InMemory provider so the count goes through real relational translation.
/// </summary>
public class AuditLogQueryAppServiceListCountTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AuditLogsDbContextApp _dbContext;

    public AuditLogQueryAppServiceListCountTests()
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
    public async Task GetListAsync_should_omit_the_total_count_by_default()
    {
        await InsertAsync(count: 3);

        var page = await CreateService().GetListAsync(new GetAuditLogsInput { MaxResultCount = 2 });

        // Null, not zero: a client must be able to tell "not counted" from "none matched".
        Assert.Null(page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task GetListAsync_should_count_the_whole_filtered_set_when_asked()
    {
        await InsertAsync(count: 3);

        var page = await CreateService().GetListAsync(new GetAuditLogsInput
        {
            MaxResultCount = 2,
            IncludeTotalCount = true
        });

        // The total spans the filter, not the page.
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task GetListAsync_should_report_zero_rather_than_null_when_a_requested_count_is_empty()
    {
        await InsertAsync(count: 2, category: "http");

        var page = await CreateService().GetListAsync(new GetAuditLogsInput
        {
            Category = "nothing-matches-this",
            IncludeTotalCount = true
        });

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetListAsync_should_still_advertise_more_results_without_a_count()
    {
        await InsertAsync(count: 5);

        var page = await CreateService().GetListAsync(new GetAuditLogsInput { MaxResultCount = 2 });

        // "More results exist" comes from the cursor, not the total, which is what makes the count
        // droppable: a client that showed "1–2 of 5" can show "more results available" instead.
        // Cursor continuation itself is covered by AuditLogCursorPaginationTests against the codec.
        Assert.Null(page.TotalCount);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task GetListAsync_should_report_no_further_results_on_a_short_page()
    {
        await InsertAsync(count: 2);

        var page = await CreateService().GetListAsync(new GetAuditLogsInput { MaxResultCount = 10 });

        // Without a total, this null is the only signal that the operator has reached the end.
        Assert.Null(page.TotalCount);
        Assert.Null(page.NextCursor);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task GetEntityChangesAsync_should_omit_the_total_count_by_default()
    {
        await InsertEntityChangesAsync(count: 3);

        var page = await CreateService().GetEntityChangesAsync(new GetAuditLogEntityChangesInput
        {
            EntityType = "Identity.Domain.Users.IdentityUser",
            EntityId = "8f2b1c44-0000-0000-0000-000000000001",
            MaxResultCount = 2
        });

        // An entity with a long change history is exactly where the scan hurts, and the workspace grid
        // pages by cursor.
        Assert.Null(page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task GetEntityChangesAsync_should_count_the_whole_filtered_set_when_asked()
    {
        await InsertEntityChangesAsync(count: 3);

        var page = await CreateService().GetEntityChangesAsync(new GetAuditLogEntityChangesInput
        {
            EntityType = "Identity.Domain.Users.IdentityUser",
            EntityId = "8f2b1c44-0000-0000-0000-000000000001",
            MaxResultCount = 2,
            IncludeTotalCount = true
        });

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
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

    private async Task InsertAsync(int count, string category = "http")
    {
        var baseTime = DateTime.SpecifyKind(new DateTime(2026, 7, 14, 9, 0, 0), DateTimeKind.Utc);

        for (var index = 0; index < count; index++)
        {
            _dbContext.AuditLogs.Add(new AuditLog(
                id: 0,
                eventId: Guid.NewGuid(),
                applicationName: "identity",
                category: category,
                tenantId: null,
                tenantName: null,
                userId: $"user-{index}",
                userName: $"user-{index}",
                executionTime: baseTime.AddMinutes(-index),
                executionDuration: 5,
                clientIpAddress: "10.0.0.1",
                clientName: null,
                clientId: null,
                correlationId: null,
                traceId: null,
                browserInfo: null,
                httpMethod: "GET",
                url: "/api/test",
                httpStatusCode: 200,
                impersonatorUserId: null,
                impersonatorUserName: null,
                impersonatorTenantId: null,
                impersonatorTenantName: null,
                exceptions: null,
                comments: null));
        }

        await _dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Entity changes are read through a join back onto their audit log, so the logs have to exist and
    /// carry the ids the changes point at. Saved in two passes because the database assigns those ids.
    /// </summary>
    private async Task InsertEntityChangesAsync(int count)
    {
        await InsertAsync(count);

        var auditLogIds = _dbContext.AuditLogs
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToList();

        var baseTime = DateTime.SpecifyKind(new DateTime(2026, 7, 14, 9, 0, 0), DateTimeKind.Utc);

        for (var index = 0; index < count; index++)
        {
            _dbContext.Set<EntityChange>().Add(new EntityChange(
                id: 0,
                auditLogId: auditLogIds[index],
                tenantId: null,
                changeTime: baseTime.AddMinutes(-index),
                changeType: "Updated",
                entityTenantId: null,
                entityId: "8f2b1c44-0000-0000-0000-000000000001",
                entityTypeFullName: "Identity.Domain.Users.IdentityUser"));
        }

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
