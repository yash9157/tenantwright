using Identity.Application.Contracts.Users;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System.Text.Json;
using Identity.Application.ApplicationUsers;
using Identity.Domain.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

internal sealed class IdentityUserImportProcessor : ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int JobBatchSize = 3;
    private const int RowChunkSize = 50;
    private const int MaxAttempts = 3;
    private const int LeaseMinutes = 5;

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ApplicationUserManager _userManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IIdentityUserImportPayloadProtector _payloadProtector;

    public IdentityUserImportProcessor(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ApplicationUserManager userManager, ICurrentTenant currentTenant, IIdentityUserImportPayloadProtector payloadProtector)
    {
        _dbContextProvider = dbContextProvider;
        _userManager = userManager;
        _currentTenant = currentTenant;
        _payloadProtector = payloadProtector;
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var utcNow = DateTime.UtcNow;
        var jobs = await unitOfWorkDbContext.IdentityUserImportJobs
            .IgnoreQueryFilters()
            .Where(x =>
                x.TryCount < MaxAttempts &&
                (x.Status == IdentityUserJobStatuses.Queued ||
                 x.Status == IdentityUserJobStatuses.Processing) &&
                (!x.NextAttemptTimeUtc.HasValue || x.NextAttemptTimeUtc <= utcNow))
            .OrderBy(x => x.NextAttemptTimeUtc ?? x.RequestedAtUtc)
            .ThenBy(x => x.Id)
            .Take(JobBatchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var job in jobs)
        {
            if (await ProcessOneAsync(job, cancellationToken))
            {
                processed++;
            }
        }

        return processed;
    }

    public async Task ProcessJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var job = await unitOfWorkDbContext.IdentityUserImportJobs
                    .IgnoreQueryFilters()
                    .SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken)
                    ?? throw new InvalidOperationException($"Identity user import job '{jobId}' was not found.");

        if (string.Equals(job.Status, IdentityUserJobStatuses.Completed, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(job.Status, IdentityUserJobStatuses.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(job.FailureReason ?? "Identity user import job failed.");
        }

        var processed = await ProcessOneAsync(job, cancellationToken, throwOnFailure: true);
        if (!processed)
        {
            var failedJob = await unitOfWorkDbContext.IdentityUserImportJobs
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == jobId, cancellationToken);
            throw new InvalidOperationException(failedJob.FailureReason ?? "Identity user import job was not processed.");
        }
    }

    private async Task<bool> ProcessOneAsync(
        IdentityUserImportJob job,
        CancellationToken cancellationToken,
        bool throwOnFailure = false)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (!job.TenantId.HasValue)
        {
            job.Status = IdentityUserJobStatuses.Failed;
            job.CompletedAtUtc = DateTime.UtcNow;
            job.NextAttemptTimeUtc = null;
            job.FailureReason = "Identity user imports require a tenant.";
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            if (throwOnFailure)
            {
                throw new InvalidOperationException(job.FailureReason);
            }

            return false;
        }

        var tenantId = job.TenantId.Value;
        using var tenantScope = _currentTenant.Change(tenantId);
        job.Status = IdentityUserJobStatuses.Processing;
        job.StartedAtUtc ??= DateTime.UtcNow;
        job.TryCount++;
        job.NextAttemptTimeUtc = DateTime.UtcNow.AddMinutes(LeaseMinutes);
        job.FailureReason = null;
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var payloadJson = _payloadProtector.Unprotect(job.ProtectedPayload);
            var payload = JsonSerializer.Deserialize<UserImportPreparedPayload>(payloadJson, JsonOptions)
                ?? throw new InvalidOperationException("The import payload is invalid.");
            if (payload.Rows.Count != job.ValidRowCount || job.CreatedCount > payload.Rows.Count)
            {
                throw new InvalidOperationException("The import progress does not match the protected payload.");
            }

            while (job.CreatedCount < payload.Rows.Count)
            {
                var chunk = payload.Rows
                    .Skip(job.CreatedCount)
                    .Take(RowChunkSize)
                    .ToList();
                await ProcessChunkAsync(job, chunk, cancellationToken);
            }

            job.Status = IdentityUserJobStatuses.Completed;
            job.CompletedAtUtc = DateTime.UtcNow;
            job.NextAttemptTimeUtc = null;
            job.FailureReason = null;
            job.ProtectedPayload = string.Empty;
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            unitOfWorkDbContext.ClearChangeTracker();
            using var retryTenantScope = _currentTenant.Change(job.TenantId);
            var failedJob = await unitOfWorkDbContext.IdentityUserImportJobs
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == job.Id, cancellationToken);
            failedJob.FailureReason = NormalizeFailureReason(exception.Message);
            if (failedJob.TryCount >= MaxAttempts)
            {
                failedJob.Status = IdentityUserJobStatuses.Failed;
                failedJob.CompletedAtUtc = DateTime.UtcNow;
                failedJob.NextAttemptTimeUtc = null;
            }
            else
            {
                failedJob.Status = IdentityUserJobStatuses.Queued;
                failedJob.NextAttemptTimeUtc = DateTime.UtcNow.AddSeconds(15 * failedJob.TryCount);
            }

            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            if (throwOnFailure)
            {
                throw new InvalidOperationException(failedJob.FailureReason ?? "Identity user import job failed.");
            }

            return false;
        }
    }

    private async Task ProcessChunkAsync(
        IdentityUserImportJob job,
        List<UserImportValidatedRow> chunk,
        CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        IDbContextTransaction? transaction = await unitOfWorkDbContext.BeginTransactionIfRelationalAsync(cancellationToken);

        await using (transaction)
        {
            try
            {
                foreach (var row in chunk)
                {
                    var user = new ApplicationUser
                    {
                        UserName = row.UserName,
                        Email = row.Email,
                        PhoneNumber = row.PhoneNumber,
                        IsActive = row.IsActive,
                        TenantId = job.TenantId!.Value
                    };
                    user.InitializeLifecycle(row.IsActive);

                    var createResult = await _userManager.CreateAsync(user, row.Password);
                    EnsureSucceeded(createResult);

                    if (row.RoleNames.Count > 0)
                    {
                        var roleResult = await _userManager.AddToRolesAsync(user, row.RoleNames);
                        EnsureSucceeded(roleResult);
                    }
                }

                job.CreatedCount += chunk.Count;
                job.NextAttemptTimeUtc = DateTime.UtcNow.AddMinutes(LeaseMinutes);
                await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                throw;
            }
        }
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                string.Join(", ", result.Errors.Select(error => error.Description)));
        }
    }

    private static string NormalizeFailureReason(string message)
    {
        var normalized = string.IsNullOrWhiteSpace(message) ? "Import processing failed." : message.Trim();
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }
}
