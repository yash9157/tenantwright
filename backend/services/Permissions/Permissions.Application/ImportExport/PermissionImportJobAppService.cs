using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.ImportExport;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Domain.ImportExport;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Domain.Repositories;
using Shared.BackgroundTasks.Jobs;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Permissions.Application.ImportExport;

/// <summary>
/// Durable permission-import jobs. Create validates and persists the payload snapshot; a valid job
/// gets a one-time apply token (only its hash + expiry are stored). Apply verifies and consumes the
/// token for the same requester, then queues the retained snapshot for transactional re-validation
/// and application. The token cannot be replayed and the worker cannot act on an unreviewed payload.
/// </summary>
public class PermissionImportJobAppService : IPermissionImportJobAppService, ITransientDependency
{
    private const string CatalogKind = "Catalog";
    private const string ProviderGrantsKind = "ProviderGrants";
    private const string ResourceGrantsKind = "ResourceGrants";
    private const string CsvFormat = "csv";
    private const string PermissionsDefaultQueue = "permissions-default";
    private static readonly TimeSpan ApplyTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long a job may sit in <c>Applying</c> before its worker is presumed dead and an
    /// operator is allowed to reclaim it via retry.</summary>
    private static readonly TimeSpan ApplyStaleAfter = TimeSpan.FromMinutes(30);

    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<PermissionImportJob, Guid> _jobRepository;
    private readonly IPermissionImportExportAppService _importExport;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public PermissionImportJobAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        IRepository<PermissionImportJob, Guid> jobRepository,
        IPermissionImportExportAppService importExport,
        IBackgroundJobManager backgroundJobManager)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _jobRepository = jobRepository;
        _importExport = importExport;
        _backgroundJobManager = backgroundJobManager;
    }

    [Audited]
    public async Task<PermissionImportJobCreatedDto> CreateAsync(CreatePermissionImportJobDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken);

        var content = input.Content ?? string.Empty;
        var kind = NormalizeKind(input.Kind);
        var scope = RequireScope(kind, input);
        var validation = await ValidateForAsync(kind, scope, content, cancellationToken);

        var job = new PermissionImportJob(
            Guid.CreateVersion7(), _currentTenant.Id, kind, CsvFormat, content, HashContent(content),
            validation.IsValid ? PermissionImportJobStatus.Validated : PermissionImportJobStatus.Invalid,
            _currentUser.Id, DateTime.UtcNow);
        if (kind == ProviderGrantsKind)
        {
            job.SetProviderScope(scope.ProviderName!, scope.ProviderKey!);
        }
        else if (kind == ResourceGrantsKind)
        {
            job.SetResourceScope(
                scope.ResourceName!,
                scope.ResourceKey!,
                scope.ProviderName!,
                scope.ProviderKey!);
        }
        job.SetValidation(job.Status, validation.TotalCount, validation.UpsertCount);

        var token = validation.IsValid ? IssueTokenFor(job) : null;
        await _jobRepository.InsertAsync(job, autoSave: true, cancellationToken: cancellationToken);

        return new PermissionImportJobCreatedDto { Job = ToDto(job), Validation = validation, ApplyToken = token };
    }

    /// <summary>
    /// Accepts an apply. The token is verified and consumed here, on the request thread, and the job
    /// moves to <see cref="PermissionImportJobStatus.Applying"/>; the re-validate-and-write itself is
    /// handed to a background worker so a large catalog cannot time out the request. Poll
    /// <see cref="GetAsync"/> for the terminal status.
    /// </summary>
    [Audited]
    public async Task<PermissionImportJobDto> ApplyAsync(Guid jobId, ApplyPermissionImportJobDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ApplyPermissionImports, cancellationToken);

        var job = await FindAsync(jobId, cancellationToken);
        VerifyApplyToken(job, input.ApplyToken);

        job.MarkApplying(DateTime.UtcNow);
        await _jobRepository.UpdateAsync(job, autoSave: true, cancellationToken: cancellationToken);

        await _backgroundJobManager.EnqueueAsync(
            new PermissionImportApplyJobArgs(job.Id),
            new BackgroundJobEnqueueOptions
            {
                Queue = PermissionsDefaultQueue,
                TenantId = job.TenantId,
                RequestedByUserId = _currentUser.Id,
            },
            cancellationToken);

        return ToDto(job);
    }

    public async Task<PagedPermissionImportJobsDto> GetListAsync(PermissionImportJobListInput input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadPermissionTransfers, cancellationToken);

        var tenantId = _currentTenant.Id;
        var queryable = (await _jobRepository.GetQueryableAsync()).Where(x => x.TenantId == tenantId);
        if (input.Status is { } status)
        {
            queryable = queryable.Where(x => x.Status == status);
        }

        var totalCount = await queryable.LongCountAsync(cancellationToken);
        var items = await queryable
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(input.SkipCount)
            .Take(Math.Clamp(input.MaxResultCount, 1, 200))
            .ToListAsync(cancellationToken);

        return new PagedPermissionImportJobsDto { TotalCount = totalCount, Items = items.Select(ToDto).ToList() };
    }

    public async Task<PermissionImportJobDto> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadPermissionTransfers, cancellationToken);
        return ToDto(await FindAsync(jobId, cancellationToken));
    }

    [Audited]
    public async Task<PermissionImportJobDto> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken);

        var job = await FindAsync(jobId, cancellationToken);
        if (job.Status != PermissionImportJobStatus.Validated)
        {
            throw Conflict(PermissionImportJobConflictCodes.NotValidated, $"Only a validated job can be cancelled (job is {job.Status}).");
        }

        job.Cancel(DateTime.UtcNow);
        await _jobRepository.UpdateAsync(job, autoSave: true, cancellationToken: cancellationToken);
        return ToDto(job);
    }

    [Audited]
    public async Task<PermissionImportJobCreatedDto> RetryAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken);

        var utcNow = DateTime.UtcNow;
        var job = await FindAsync(jobId, cancellationToken);
        if (job.Status == PermissionImportJobStatus.Applied)
        {
            throw Conflict(PermissionImportJobConflictCodes.NotValidated, "An applied import job cannot be retried.");
        }

        if (job.Status == PermissionImportJobStatus.Applying && !job.IsApplyStalled(utcNow, ApplyStaleAfter))
        {
            throw Conflict(PermissionImportJobConflictCodes.Applying, "The import is still applying. Wait for it to finish before retrying.");
        }

        job.ResetForRetry(utcNow, ApplyStaleAfter);
        var validation = await ValidateForAsync(
            job.Kind,
            new ImportScope(job.ProviderName, job.ProviderKey, job.ResourceName, job.ResourceKey),
            job.Content,
            cancellationToken);
        job.SetValidation(validation.IsValid ? PermissionImportJobStatus.Validated : PermissionImportJobStatus.Invalid, validation.TotalCount, validation.UpsertCount);

        var token = validation.IsValid ? IssueTokenFor(job) : null;
        await _jobRepository.UpdateAsync(job, autoSave: true, cancellationToken: cancellationToken);
        return new PermissionImportJobCreatedDto { Job = ToDto(job), Validation = validation, ApplyToken = token };
    }

    private async Task<PermissionImportJob> FindAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var tenantId = _currentTenant.Id;
        return await _jobRepository.FindAsync(x => x.Id == jobId && x.TenantId == tenantId, cancellationToken: cancellationToken)
            ?? throw Conflict(PermissionImportJobConflictCodes.NotFound, "Import job not found.");
    }

    private static string IssueTokenFor(PermissionImportJob job)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        job.IssueApplyToken(HashToken(token), DateTime.UtcNow.Add(ApplyTokenLifetime));
        return token;
    }

    private void VerifyApplyToken(PermissionImportJob job, string? presentedToken)
    {
        if (job.Status != PermissionImportJobStatus.Validated)
        {
            throw Conflict(PermissionImportJobConflictCodes.NotValidated, $"Job is {job.Status}; only a validated job can be applied.");
        }

        if (job.RequestedByUserId is not null && job.RequestedByUserId != _currentUser.Id)
        {
            throw Conflict(PermissionImportJobConflictCodes.InvalidToken, "The apply token does not belong to the current requester.");
        }

        if (job.ApplyTokenExpiresAtUtc is null || job.ApplyTokenExpiresAtUtc.Value <= DateTime.UtcNow)
        {
            throw Conflict(PermissionImportJobConflictCodes.TokenExpired, "The apply token has expired. Re-validate to get a new one.");
        }

        var presentedHash = HashToken((presentedToken ?? string.Empty).Trim());
        var expected = job.ApplyTokenHash ?? string.Empty;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presentedHash), Encoding.UTF8.GetBytes(expected)))
        {
            throw Conflict(PermissionImportJobConflictCodes.InvalidToken, "The apply token is invalid.");
        }
    }

    private static string NormalizeKind(string? kind)
    {
        var value = kind?.Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(value, CatalogKind, StringComparison.OrdinalIgnoreCase))
        {
            return CatalogKind;
        }

        if (string.Equals(value, ProviderGrantsKind, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderGrantsKind;
        }

        if (string.Equals(value, ResourceGrantsKind, StringComparison.OrdinalIgnoreCase))
        {
            return ResourceGrantsKind;
        }

        throw new ArgumentException($"Unsupported permission import kind '{value}'.", nameof(kind));
    }

    private static ImportScope RequireScope(string kind, CreatePermissionImportJobDto input)
    {
        if (kind == CatalogKind)
        {
            return ImportScope.Empty;
        }

        var name = input.ProviderName?.Trim();
        var key = input.ProviderKey?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("ProviderName and ProviderKey are required for grant imports.", nameof(input));
        }

        if (kind == ProviderGrantsKind)
        {
            return new ImportScope(name, key, null, null);
        }

        var resourceName = input.ResourceName?.Trim();
        var resourceKey = input.ResourceKey?.Trim();
        if (string.IsNullOrEmpty(resourceName) || string.IsNullOrEmpty(resourceKey))
        {
            throw new ArgumentException("ResourceName and ResourceKey are required for a ResourceGrants import.", nameof(input));
        }

        return new ImportScope(name, key, resourceName, resourceKey);
    }

    private Task<PermissionImportValidationResultDto> ValidateForAsync(
        string kind,
        ImportScope scope,
        string content,
        CancellationToken cancellationToken)
    {
        var payload = new PermissionCsvImportRequestDto { Content = content };
        return kind switch
        {
            ProviderGrantsKind => _importExport.ValidateProviderGrantCsvImportAsync(
                scope.ProviderName!, scope.ProviderKey!, payload, cancellationToken),
            ResourceGrantsKind => _importExport.ValidateResourcePermissionCsvImportAsync(
                scope.ResourceName!, scope.ResourceKey!, scope.ProviderName!, scope.ProviderKey!, payload, cancellationToken),
            _ => _importExport.ValidateCatalogCsvImportAsync(payload, cancellationToken),
        };
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string HashContent(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static PermissionImportJobConflictException Conflict(string code, string message) =>
        new(new PermissionImportJobConflictDto { Code = code, Message = message });

    private static PermissionImportJobDto ToDto(PermissionImportJob job) => new()
    {
        Id = job.Id,
        TenantId = job.TenantId,
        Kind = job.Kind,
        Format = job.Format,
        ProviderName = job.ProviderName,
        ProviderKey = job.ProviderKey,
        ResourceName = job.ResourceName,
        ResourceKey = job.ResourceKey,
        ContentHash = job.ContentHash,
        Status = job.Status,
        TotalCount = job.TotalCount,
        UpsertCount = job.UpsertCount,
        CreatedCount = job.CreatedCount,
        UpdatedCount = job.UpdatedCount,
        RemovedCount = job.RemovedCount,
        UnchangedCount = job.UnchangedCount,
        RequestedByUserId = job.RequestedByUserId,
        CreatedAtUtc = job.CreatedAtUtc,
        StartedAtUtc = job.StartedAtUtc,
        CompletedAtUtc = job.CompletedAtUtc,
        FailureReason = job.FailureReason,
        ApplyTokenExpiresAtUtc = job.ApplyTokenExpiresAtUtc,
        HasPendingApplyToken = job.ApplyTokenHash is not null && job.ApplyTokenExpiresAtUtc > DateTime.UtcNow,
        Version = job.Version,
    };

    private sealed record ImportScope(
        string? ProviderName,
        string? ProviderKey,
        string? ResourceName,
        string? ResourceKey)
    {
        public static readonly ImportScope Empty = new(null, null, null, null);
    }
}
