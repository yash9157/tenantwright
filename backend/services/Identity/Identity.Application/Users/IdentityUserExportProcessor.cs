using Identity.Application.Contracts.Users;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using Identity.Application.ApplicationUsers;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

public sealed class IdentityUserExportProcessor : ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int BatchSize = 3;

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;

    public IdentityUserExportProcessor(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var utcNow = DateTime.UtcNow;
        var jobs = await unitOfWorkDbContext.IdentityUserExportJobs
            .IgnoreQueryFilters()
            .Where(x =>
                x.Status == IdentityUserJobStatuses.Queued &&
                (!x.NextAttemptTimeUtc.HasValue || x.NextAttemptTimeUtc.Value <= utcNow))
            .OrderBy(x => x.NextAttemptTimeUtc ?? x.RequestedAtUtc)
            .ThenBy(x => x.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var job in jobs)
        {
            if (await ProcessOneAsync(job, cancellationToken))
            {
                processed++;
            }
        }

        await CleanupExpiredAsync(cancellationToken);
        return processed;
    }

    public async Task ProcessJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var job = await unitOfWorkDbContext.IdentityUserExportJobs
                    .IgnoreQueryFilters()
                    .SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken)
                    ?? throw new InvalidOperationException($"Identity user export job '{jobId}' was not found.");

        if (string.Equals(job.Status, IdentityUserJobStatuses.Completed, StringComparison.Ordinal) ||
            string.Equals(job.Status, IdentityUserJobStatuses.Expired, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(job.Status, IdentityUserJobStatuses.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(job.FailureReason ?? "Identity user export job failed.");
        }

        var processed = await ProcessOneAsync(job, cancellationToken);
        await CleanupExpiredAsync(cancellationToken);
        if (!processed)
        {
            var failedJob = await unitOfWorkDbContext.IdentityUserExportJobs
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == jobId, cancellationToken);
            throw new InvalidOperationException(failedJob.FailureReason ?? "Identity user export job was not processed.");
        }
    }

    private async Task<bool> ProcessOneAsync(IdentityUserExportJob job, CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (!string.Equals(job.Status, IdentityUserJobStatuses.Queued, StringComparison.Ordinal))
        {
            return false;
        }

        if (!job.TenantId.HasValue)
        {
            job.Status = IdentityUserJobStatuses.Failed;
            job.CompletedAtUtc = DateTime.UtcNow;
            job.FailureReason = "Identity user exports require a tenant.";
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        using var tenantScope = _currentTenant.Change(job.TenantId.Value);
        job.Status = IdentityUserJobStatuses.Processing;
        job.StartedAtUtc = DateTime.UtcNow;
        job.TryCount++;
        job.NextAttemptTimeUtc = null;
        job.FailureReason = null;
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var snapshot = JsonSerializer.Deserialize<UserExportFilterSnapshot>(job.FilterSnapshotJson, JsonOptions)
                           ?? new UserExportFilterSnapshot();

            var query = unitOfWorkDbContext.Users
                .AsNoTracking()
                .Where(x => x.TenantId == job.TenantId);

            if (!string.IsNullOrWhiteSpace(snapshot.Filter))
            {
                var filter = snapshot.Filter;
                query = query.Where(x =>
                    x.UserName!.Contains(filter) ||
                    x.Email!.Contains(filter) ||
                    (x.PhoneNumber != null && x.PhoneNumber.Contains(filter)));
            }

            if (snapshot.IsActive.HasValue)
            {
                query = snapshot.IsActive.Value
                    ? query.Where(x => x.IsActive && x.LifecycleStatus == IdentityUserLifecycleStatus.Active)
                    : query.Where(x => !x.IsActive || x.LifecycleStatus != IdentityUserLifecycleStatus.Active);
            }

            if (!snapshot.RequestedByHostAdmin)
            {
                var elevatedRoleNames = new[]
                {
                    TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
                };

                var elevatedUserIds = from userRole in unitOfWorkDbContext.UserRoles
                                      join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
                                      where role.TenantId == job.TenantId && elevatedRoleNames.Contains(role.NormalizedName!)
                                      select userRole.UserId;

                query = query.Where(x => !elevatedUserIds.Contains(x.Id));
            }

            if (!string.IsNullOrWhiteSpace(snapshot.RoleName))
            {
                var normalizedRoleName = snapshot.RoleName.ToUpperInvariant();
                var userIdsInRole = from userRole in unitOfWorkDbContext.UserRoles
                                    join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
                                    where role.TenantId == job.TenantId && role.NormalizedName == normalizedRoleName
                                    select userRole.UserId;

                query = query.Where(x => userIdsInRole.Contains(x.Id));
            }

            var users = await query
                .OrderBy(x => x.UserName)
                .Take(Math.Clamp(snapshot.MaxRowCount, 1, 5000))
                .Select(x => new ExportRow
                {
                    Id = x.Id,
                    UserName = x.UserName!,
                    Email = x.Email!,
                    PhoneNumber = x.PhoneNumber,
                    IsActive = x.IsActive && x.LifecycleStatus == IdentityUserLifecycleStatus.Active
                })
                .ToListAsync(cancellationToken);

            var userIds = users.Select(x => x.Id).ToList();
            var roleLookup = await (from userRole in unitOfWorkDbContext.UserRoles
                                    join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
                                    where userIds.Contains(userRole.UserId)
                                    select new { userRole.UserId, role.Name })
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var rolesByUserId = roleLookup
                .GroupBy(x => x.UserId)
                .ToDictionary(
                    x => x.Key,
                    x => TenantWrightIdentityRoleNames.NormalizeMany(x.Select(r => r.Name!)).OrderBy(r => r).ToList());

            var builder = new StringBuilder();
            builder.AppendLine("UserName,Email,PhoneNumber,IsActive,RoleNames");
            foreach (var user in users)
            {
                rolesByUserId.TryGetValue(user.Id, out var roles);
                builder.AppendLine(string.Join(",",
                    EscapeCsv(user.UserName),
                    EscapeCsv(user.Email),
                    EscapeCsv(user.PhoneNumber),
                    user.IsActive ? "true" : "false",
                    EscapeCsv(string.Join(';', roles ?? []))));
            }

            var content = Encoding.UTF8.GetBytes(builder.ToString());
            job.Status = IdentityUserJobStatuses.Completed;
            job.CompletedAtUtc = DateTime.UtcNow;
            job.FileName = $"users-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
            job.ContentType = "text/csv";
            job.FileContent = content;
            job.FileSizeBytes = content.LongLength;
            job.RowCount = users.Count;
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            job.Status = IdentityUserJobStatuses.Failed;
            job.CompletedAtUtc = DateTime.UtcNow;
            job.FailureReason = ex.Message.Length <= 512 ? ex.Message : ex.Message[..512];
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            return false;
        }
    }

    private async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var utcNow = DateTime.UtcNow;
        var expiredJobs = await unitOfWorkDbContext.IdentityUserExportJobs
            .IgnoreQueryFilters()
            .Where(x => x.ExpireAtUtc <= utcNow)
            .ToListAsync(cancellationToken);

        foreach (var job in expiredJobs)
        {
            job.Status = IdentityUserJobStatuses.Expired;
            job.FileContent = null;
            job.FileName = null;
            job.ContentType = null;
            job.RowCount = null;
            job.FileSizeBytes = null;
        }

        if (expiredJobs.Count > 0)
        {
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Contains(',', StringComparison.Ordinal) && !value.Contains('"', StringComparison.Ordinal) && !value.Contains('\n', StringComparison.Ordinal) && !value.Contains('\r', StringComparison.Ordinal))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private sealed class ExportRow
    {
        public Guid Id { get; init; }
        public string UserName { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string? PhoneNumber { get; init; }
        public bool IsActive { get; init; }
    }
}
