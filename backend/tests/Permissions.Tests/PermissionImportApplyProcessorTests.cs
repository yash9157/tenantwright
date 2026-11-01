using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.ImportExport;
using Permissions.Domain.ImportExport;
using Permissions.Domain.Shared.Enums;
using Permissions.Tests.TestDoubles;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionImportApplyProcessorTests
{
    [Fact]
    public async Task Resource_grant_job_applies_the_retained_resource_and_provider_scope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null);
        await context.Database.EnsureCreatedAsync();

        var job = new PermissionImportJob(
            Guid.NewGuid(),
            tenantId: null,
            "ResourceGrants",
            "csv",
            "permission,effect\nDocuments.Read,Allow",
            "content-hash",
            PermissionImportJobStatus.Validated,
            Guid.NewGuid(),
            DateTime.UtcNow);
        job.SetResourceScope("Document", "document-42", "User", "user-7");
        job.SetValidation(PermissionImportJobStatus.Validated, totalCount: 1, upsertCount: 1);
        job.IssueApplyToken("token-hash", DateTime.UtcNow.AddMinutes(15));
        job.MarkApplying(DateTime.UtcNow);
        context.PermissionImportJobs.Add(job);
        await context.SaveChangesAsync();

        var importExport = new RecordingPermissionImportExportAppService();
        var transaction = new PermissionImportApplyTransaction(
            new EfTestRepository<PermissionImportJob, Guid>(context),
            importExport,
            NullLogger<PermissionImportApplyTransaction>.Instance);
        var processor = new PermissionImportApplyProcessor(
            transaction,
            NullLogger<PermissionImportApplyProcessor>.Instance);

        await processor.ProcessAsync(job.Id);

        Assert.Equal(("Document", "document-42", "User", "user-7"), importExport.ResourceScope);
        Assert.Equal(PermissionImportJobStatus.Applied, job.Status);
        Assert.Equal(1, job.CreatedCount);
    }

    [Fact]
    public async Task Apply_failure_is_recorded_through_a_separate_transaction_call()
    {
        var jobId = Guid.NewGuid();
        var transaction = new RecordingApplyTransaction
        {
            ApplyException = new InvalidOperationException("Expected apply failure."),
        };
        var processor = new PermissionImportApplyProcessor(
            transaction,
            NullLogger<PermissionImportApplyProcessor>.Instance);

        await processor.ProcessAsync(jobId);

        Assert.Equal(jobId, transaction.AppliedJobId);
        Assert.Equal(jobId, transaction.FailedJobId);
        Assert.Equal("The import failed while applying. Re-validate and try again.", transaction.FailureReason);
    }

    [Fact]
    public async Task Cancellation_does_not_record_a_terminal_failure()
    {
        var transaction = new RecordingApplyTransaction
        {
            ApplyException = new OperationCanceledException(),
        };
        var processor = new PermissionImportApplyProcessor(
            transaction,
            NullLogger<PermissionImportApplyProcessor>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(Guid.NewGuid()));

        Assert.Null(transaction.FailedJobId);
    }

    private sealed class RecordingApplyTransaction : IPermissionImportApplyTransaction
    {
        public Exception? ApplyException { get; init; }
        public Guid? AppliedJobId { get; private set; }
        public Guid? FailedJobId { get; private set; }
        public string? FailureReason { get; private set; }

        public Task ApplyAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            AppliedJobId = jobId;
            return ApplyException == null ? Task.CompletedTask : Task.FromException(ApplyException);
        }

        public Task RecordFailureAsync(
            Guid jobId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            FailedJobId = jobId;
            FailureReason = reason;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPermissionImportExportAppService : IPermissionImportExportAppService
    {
        public (string ResourceName, string ResourceKey, string ProviderName, string ProviderKey)? ResourceScope { get; private set; }

        public Task<PermissionImportApplyResultDto> ApplyResourcePermissionCsvImportAsync(
            string resourceName,
            string resourceKey,
            string providerName,
            string providerKey,
            PermissionCsvImportRequestDto input,
            CancellationToken cancellationToken = default)
        {
            ResourceScope = (resourceName, resourceKey, providerName, providerKey);
            return Task.FromResult(new PermissionImportApplyResultDto
            {
                IsValid = true,
                TotalCount = 1,
                CreatedCount = 1,
            });
        }

        public Task<PermissionCatalogExportDto> ExportCatalogAsync(CancellationToken cancellationToken = default) => NotUsed<PermissionCatalogExportDto>();
        public Task<PermissionExportFileDto> ExportCatalogFileAsync(PermissionExportFormat format, CancellationToken cancellationToken = default) => NotUsed<PermissionExportFileDto>();
        public Task<PermissionImportValidationResultDto> ValidateCatalogImportAsync(PermissionCatalogImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportValidationResultDto>();
        public Task<PermissionImportApplyResultDto> ApplyCatalogImportAsync(PermissionCatalogImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportApplyResultDto>();
        public Task<PermissionImportValidationResultDto> ValidateCatalogCsvImportAsync(PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportValidationResultDto>();
        public Task<PermissionImportApplyResultDto> ApplyCatalogCsvImportAsync(PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportApplyResultDto>();
        public Task<PermissionProviderGrantExportDto> ExportProviderGrantsAsync(string providerName, string providerKey, CancellationToken cancellationToken = default) => NotUsed<PermissionProviderGrantExportDto>();
        public Task<PermissionExportFileDto> ExportProviderGrantsFileAsync(string providerName, string providerKey, PermissionExportFormat format, CancellationToken cancellationToken = default) => NotUsed<PermissionExportFileDto>();
        public Task<PermissionImportValidationResultDto> ValidateProviderGrantImportAsync(string providerName, string providerKey, PermissionProviderGrantImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportValidationResultDto>();
        public Task<PermissionImportApplyResultDto> ApplyProviderGrantImportAsync(string providerName, string providerKey, PermissionProviderGrantImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportApplyResultDto>();
        public Task<PermissionImportValidationResultDto> ValidateProviderGrantCsvImportAsync(string providerName, string providerKey, PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportValidationResultDto>();
        public Task<PermissionImportApplyResultDto> ApplyProviderGrantCsvImportAsync(string providerName, string providerKey, PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportApplyResultDto>();
        public Task<PermissionResourceGrantExportDto> ExportResourcePermissionsAsync(string resourceName, string resourceKey, string providerName, string providerKey, CancellationToken cancellationToken = default) => NotUsed<PermissionResourceGrantExportDto>();
        public Task<PermissionExportFileDto> ExportResourcePermissionsFileAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionExportFormat format, CancellationToken cancellationToken = default) => NotUsed<PermissionExportFileDto>();
        public Task<PermissionImportValidationResultDto> ValidateResourcePermissionImportAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionResourceGrantImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportValidationResultDto>();
        public Task<PermissionImportApplyResultDto> ApplyResourcePermissionImportAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionResourceGrantImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportApplyResultDto>();
        public Task<PermissionImportValidationResultDto> ValidateResourcePermissionCsvImportAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default) => NotUsed<PermissionImportValidationResultDto>();

        private static Task<T> NotUsed<T>() => throw new NotSupportedException();
    }
}
