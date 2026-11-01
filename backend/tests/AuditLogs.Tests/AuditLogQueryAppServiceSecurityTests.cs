using AuditLogs.Application.Auditing;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.Tests.TestDoubles;
using Microsoft.Extensions.Options;
using Moq;
using Shared.BackgroundTasks.Jobs;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain.Repositories;
using Shared.MultiTenancy;
using Xunit;

namespace AuditLogs.Tests;

public partial class AuditLogQueryAppServiceSecurityTests
{
    [Fact]
    public async Task GetAsync_should_redact_sensitive_payload_even_when_exception_output_is_enabled()
    {
        var auditLog = CreateAuditLogWithException();
        var queryStore = new Mock<AuditLogQueryStore>(null!);
        queryStore
            .Setup(x => x.FindDetailsAsync(auditLog.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditLog);

        var service = new AuditLogQueryAppService(
            queryStore: queryStore.Object,
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: new Mock<IRepository<AuditLogExport, long>>().Object,
            currentTenant: new TestCurrentTenant(null),
            currentUser: new TestCurrentUser(),
            dataFilter: new TestDataFilter(),
            securityOptions: Options.Create(new AuditLogSecurityOptions
            {
                SendExceptionDetailsToClients = true,
                SendExceptionTypeToClients = true,
                SendExceptionStackTraceToClients = true
            }),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: new NoopBackgroundJobManager(),
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());

        var detail = await service.GetAsync(auditLog.Id);

        Assert.True(detail.SensitivePayloadRedacted);
        Assert.All(detail.Actions, action =>
        {
            Assert.Equal("Sensitive payload hidden.", action.Parameters);
            Assert.Null(action.Result);
        });
        Assert.Empty(detail.Exceptions);
        Assert.Empty(detail.ExtraProperties);
    }

    [Fact]
    public async Task GetSensitivePayloadAsync_should_hide_exception_details_by_default()
    {
        var auditLog = CreateAuditLogWithException();
        var queryStore = new Mock<AuditLogQueryStore>(null!);
        queryStore
            .Setup(x => x.FindDetailsAsync(auditLog.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditLog);

        var service = new AuditLogQueryAppService(
            queryStore: queryStore.Object,
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: new Mock<IRepository<AuditLogExport, long>>().Object,
            currentTenant: new TestCurrentTenant(null),
            currentUser: new TestCurrentUser(),
            dataFilter: new TestDataFilter(),
            securityOptions: Options.Create(new AuditLogSecurityOptions()),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: new NoopBackgroundJobManager(),
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());

        var detail = await service.GetSensitivePayloadAsync(auditLog.Id);

        Assert.Single(detail.Exceptions);
        Assert.Equal("error", detail.Exceptions[0].Type);
        Assert.Equal("Exception details are hidden.", detail.Exceptions[0].Message);
        Assert.Null(detail.Exceptions[0].StackTrace);
    }

    [Fact]
    public async Task GetSensitivePayloadAsync_should_return_exception_details_when_enabled()
    {
        var auditLog = CreateAuditLogWithException();
        var queryStore = new Mock<AuditLogQueryStore>(null!);
        queryStore
            .Setup(x => x.FindDetailsAsync(auditLog.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditLog);

        var options = new AuditLogSecurityOptions
        {
            SendExceptionDetailsToClients = true,
            SendExceptionTypeToClients = true,
            SendExceptionStackTraceToClients = true
        };

        var service = new AuditLogQueryAppService(
            queryStore: queryStore.Object,
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: new Mock<IRepository<AuditLogExport, long>>().Object,
            currentTenant: new TestCurrentTenant(null),
            currentUser: new TestCurrentUser(),
            dataFilter: new TestDataFilter(),
            securityOptions: Options.Create(options),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: new NoopBackgroundJobManager(),
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());

        var detail = await service.GetSensitivePayloadAsync(auditLog.Id);

        Assert.Single(detail.Exceptions);
        Assert.Equal("System.InvalidOperationException", detail.Exceptions[0].Type);
        Assert.Equal("bad input", detail.Exceptions[0].Message);
        Assert.Equal("stack-line", detail.Exceptions[0].StackTrace);
    }

    [Fact]
    public async Task GetAsync_should_reject_tenant_context_before_querying_details()
    {
        var auditLog = CreateAuditLogWithException();
        var queryStore = new Mock<AuditLogQueryStore>(null!);
        queryStore
            .Setup(x => x.FindDetailsAsync(auditLog.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditLog);

        var service = new AuditLogQueryAppService(
            queryStore: queryStore.Object,
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: new Mock<IRepository<AuditLogExport, long>>().Object,
            currentTenant: new TestCurrentTenant(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
            currentUser: new TestCurrentUser(),
            dataFilter: new TestDataFilter(),
            securityOptions: Options.Create(new AuditLogSecurityOptions()),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: new NoopBackgroundJobManager(),
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(auditLog.Id));
        queryStore.Verify(
            x => x.FindDetailsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAsync_should_reject_a_tenant_claim_even_when_current_tenant_is_absent()
    {
        var queryStore = new Mock<AuditLogQueryStore>(null!);
        var service = CreateService(
            queryStore.Object,
            new TestCurrentTenant(null),
            new TestCurrentUser(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(7));
        queryStore.Verify(
            x => x.FindDetailsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static AuditLog CreateAuditLogWithException()
    {
        var auditLog = new AuditLog(
            id: 7,
            eventId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            applicationName: "identity",
            category: "http",
            tenantId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            tenantName: "tenant",
            userId: "user-1",
            userName: "Jane",
            executionTime: DateTime.UtcNow,
            executionDuration: 11,
            clientIpAddress: null,
            clientName: null,
            clientId: null,
            correlationId: null,
            traceId: null,
            browserInfo: null,
            httpMethod: "GET",
            url: "/api",
            httpStatusCode: 500,
            impersonatorUserId: null,
            impersonatorUserName: null,
            impersonatorTenantId: null,
            impersonatorTenantName: null,
            exceptions: "[{\"Type\":\"System.InvalidOperationException\",\"Message\":\"bad input\",\"StackTrace\":\"stack-line\"}]",
            comments: null);

        return auditLog;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => new DisposeAction(() => Id = id);
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        private readonly Guid? _tenantId;

        public TestCurrentUser(Guid? tenantId = null)
        {
            _tenantId = tenantId;
        }

        public bool IsAuthenticated => true;
        public Guid? Id => Guid.Parse("11111111-1111-1111-1111-111111111111");
        public string? UserName => "user";
        public string? Name => "user";
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId => _tenantId;
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


}


public partial class AuditLogQueryAppServiceSecurityTests
{
    [Fact]
    public async Task GetEntityChangeAsync_should_return_property_diffs_for_the_resolved_tenant()
    {
        var tenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var entityChange = new EntityChange(
            id: 11,
            auditLogId: 7,
            tenantId: tenantId,
            changeTime: DateTime.UtcNow,
            changeType: "Updated",
            entityTenantId: tenantId,
            entityId: "user-42",
            entityTypeFullName: "Identity.Users.IdentityUser");
        entityChange.PropertyChanges.Add(new EntityPropertyChange(
            id: 12,
            entityChangeId: entityChange.Id,
            tenantId: tenantId,
            propertyName: "DisplayName",
            propertyTypeFullName: "System.String",
            originalValue: "Before",
            newValue: "After"));

        var queryStore = new Mock<AuditLogQueryStore>(null!);
        queryStore
            .Setup(x => x.FindEntityChangeDetailsAsync(entityChange.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityChange);
        var service = CreateService(queryStore.Object, new TestCurrentTenant(null));

        var detail = await service.GetEntityChangeAsync(entityChange.Id, tenantId);

        Assert.Equal(entityChange.Id, detail.Id);
        var property = Assert.Single(detail.PropertyChanges);
        Assert.Equal("DisplayName", property.PropertyName);
        Assert.Equal("Before", property.OriginalValue);
        Assert.Equal("After", property.NewValue);
    }

    [Fact]
    public async Task GetEntityChangeAsync_should_block_a_different_tenant_before_the_query()
    {
        var queryStore = new Mock<AuditLogQueryStore>(null!);
        var service = CreateService(
            queryStore.Object,
            new TestCurrentTenant(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetEntityChangeAsync(
            id: 11,
            tenantId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));

        queryStore.Verify(
            x => x.FindEntityChangeDetailsAsync(It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static AuditLogQueryAppService CreateService(
        AuditLogQueryStore queryStore,
        ICurrentTenant currentTenant,
        ICurrentUser? currentUser = null)
    {
        return new AuditLogQueryAppService(
            queryStore: queryStore,
            archiveManifestRepository: new Mock<IRepository<AuditArchiveManifest, long>>().Object,
            archiveRestoreJobRepository: new Mock<IRepository<AuditArchiveRestoreJob, long>>().Object,
            sealVerificationRunRepository: new Mock<IRepository<AuditLogSealVerificationRun, long>>().Object,
            exportRepository: new Mock<IRepository<AuditLogExport, long>>().Object,
            currentTenant: currentTenant,
            currentUser: currentUser ?? new TestCurrentUser(),
            dataFilter: new TestDataFilter(),
            securityOptions: Options.Create(new AuditLogSecurityOptions()),
            archiveOptions: Options.Create(new AuditLogArchiveOptions()),
            backgroundJobManager: new NoopBackgroundJobManager(),
            signer: AuditLogTestSignerFactory.CreateDisabled(),
            sealService: AuditLogTestSealServiceFactory.CreateDisabled(),
            tenantDatabases: SharedOnlyTenantDatabases.FanOut());
    }
}
