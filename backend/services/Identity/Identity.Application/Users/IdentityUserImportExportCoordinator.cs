using Identity.Application.Contracts.Users;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Application.ApplicationUsers;
using Identity.Application.Tenancy;
using Identity.Domain.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using Shared;
using Shared.BackgroundTasks.Jobs;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

internal sealed class IdentityUserImportExportCoordinator : ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int DefaultExportRowCount = 1000;
    private const int MaxExportRowCount = 5000;
    private const int MaxImportRowCount = 1000;
    private const int MaxImportCsvLength = 100000;
    private const int MaxProtectedImportPayloadLength = 200000;
    private const string IdentityDefaultQueue = "identity-default";

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly TenantUserManagementGuard _managementGuard;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;
    private readonly IIdentityUserImportPayloadProtector _importPayloadProtector;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public IdentityUserImportExportCoordinator(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, TenantUserManagementGuard managementGuard, ICurrentUser currentUser, ICurrentTenant currentTenant, IIdentityUserImportPayloadProtector importPayloadProtector, IBackgroundJobManager backgroundJobManager)
    {
        _dbContextProvider = dbContextProvider;
        _managementGuard = managementGuard;
        _currentUser = currentUser;
        _currentTenant = currentTenant;
        _importPayloadProtector = importPayloadProtector;
        _backgroundJobManager = backgroundJobManager;
    }

    public async Task<UserExportJobDto> CreateExportAsync(RequestUserExportInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var requestedByUserId = _currentUser.Id ?? throw new SharedUnauthorizedException("User is not authenticated.");

        input ??= new RequestUserExportInput();
        if (!string.Equals(input.Format?.Trim(), "csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new SharedValidationException("Only csv export is supported.");
        }

        var normalizedRoleName = TenantWrightIdentityRoleNames.Normalize(input.RoleName);
        if (!string.IsNullOrWhiteSpace(normalizedRoleName))
        {
            _managementGuard.EnsureCanAssignRoles([normalizedRoleName]);
        }

        var token = CreateDownloadToken();
        var utcNow = DateTime.UtcNow;
        var snapshot = new UserExportFilterSnapshot
        {
            Filter = NormalizeOptional(input.Filter),
            IsActive = input.IsActive,
            RoleName = normalizedRoleName,
            MaxRowCount = Math.Clamp(input.MaxRowCount ?? DefaultExportRowCount, 1, MaxExportRowCount),
            RequestedByHostAdmin = _managementGuard.IsCurrentUserHostAdmin()
        };

        var job = new IdentityUserExportJob
        {
            TenantId = tenantId,
            RequestedByUserId = requestedByUserId,
            Status = IdentityUserJobStatuses.Queued,
            Format = "csv",
            FilterSnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            MaxRowCount = snapshot.MaxRowCount,
            RequestedAtUtc = utcNow,
            NextAttemptTimeUtc = utcNow,
            ExpireAtUtc = utcNow.AddHours(12),
            DownloadTokenHash = ComputeSha256Hex(token),
            DownloadTokenExpiresAtUtc = utcNow.AddHours(12)
        };

        await unitOfWorkDbContext.AddAsync(job);
        await unitOfWorkDbContext.SaveChangesAsync();
        await _backgroundJobManager.EnqueueAsync(
            new IdentityUserExportJobArgs(job.TenantId, job.Id, requestedByUserId),
            CreateEnqueueOptions(job.TenantId, requestedByUserId));

        var dto = MapExportJob(job);
        dto.DownloadToken = token;
        return dto;
    }

    public async Task<UserExportJobDto> GetExportAsync(long id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();

        var job = await unitOfWorkDbContext.IdentityUserExportJobs.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (job == null)
        {
            throw new EntityNotFoundException(typeof(IdentityUserExportJob), id);
        }

        return MapExportJob(job);
    }

    public async Task<UserExportDownloadDto> GetExportDownloadAsync(long id, string token)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();

        var job = await unitOfWorkDbContext.IdentityUserExportJobs.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (job == null)
        {
            throw new EntityNotFoundException(typeof(IdentityUserExportJob), id);
        }

        if (!string.Equals(job.Status, IdentityUserJobStatuses.Completed, StringComparison.Ordinal) || job.FileContent == null)
        {
            throw new SharedValidationException("Export is not ready for download.");
        }

        if (job.DownloadTokenExpiresAtUtc < DateTime.UtcNow || !string.Equals(job.DownloadTokenHash, ComputeSha256Hex(token), StringComparison.Ordinal))
        {
            throw new SharedUnauthorizedException("Invalid export download token.");
        }

        return new UserExportDownloadDto
        {
            FileName = job.FileName ?? $"users-{job.Id}.csv",
            ContentType = job.ContentType ?? "text/csv",
            Content = job.FileContent
        };
    }

    public async Task<UserImportValidationDto> ValidateImportAsync(ValidateUserImportInput input)
    {
        var prepared = await PrepareImportAsync(input?.FileName, input?.CsvContent);
        return prepared.Validation;
    }

    public async Task<UserImportJobDto> StartImportAsync(StartUserImportInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var requestedByUserId = _currentUser.Id ?? throw new SharedUnauthorizedException("User is not authenticated.");

        var prepared = await PrepareImportAsync(input?.FileName, input?.CsvContent);
        if (prepared.Validation.Errors.Count > 0)
        {
            throw new SharedValidationException("Import validation failed. Run validate first and fix the reported rows.");
        }

        var utcNow = DateTime.UtcNow;
        var protectedPayload = _importPayloadProtector.Protect(
            JsonSerializer.Serialize(prepared.Payload, JsonOptions));
        if (protectedPayload.Length > MaxProtectedImportPayloadLength)
        {
            throw new SharedValidationException("The protected import payload is too large.");
        }

        var job = new IdentityUserImportJob
        {
            TenantId = tenantId,
            RequestedByUserId = requestedByUserId,
            Status = IdentityUserJobStatuses.Queued,
            FileName = NormalizeOptional(input?.FileName),
            ProtectedPayload = protectedPayload,
            ErrorSnapshotJson = "[]",
            TotalRowCount = prepared.Validation.TotalRowCount,
            ValidRowCount = prepared.Validation.ValidRowCount,
            RequestedAtUtc = utcNow,
            NextAttemptTimeUtc = utcNow
        };

        await unitOfWorkDbContext.AddAsync(job);
        await unitOfWorkDbContext.SaveChangesAsync();
        await _backgroundJobManager.EnqueueAsync(
            new IdentityUserImportJobArgs(job.TenantId, job.Id, requestedByUserId),
            CreateEnqueueOptions(job.TenantId, requestedByUserId));

        return MapImportJob(job);
    }

    public async Task<UserImportJobDto> GetImportAsync(long id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();

        var job = await unitOfWorkDbContext.IdentityUserImportJobs.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (job == null)
        {
            throw new EntityNotFoundException(typeof(IdentityUserImportJob), id);
        }

        return MapImportJob(job);
    }

    internal static UserExportJobDto MapExportJob(IdentityUserExportJob job)
    {
        return new UserExportJobDto
        {
            Id = job.Id,
            TenantId = job.TenantId,
            Status = job.Status,
            Format = job.Format,
            RequestedAtUtc = job.RequestedAtUtc,
            StartedAtUtc = job.StartedAtUtc,
            CompletedAtUtc = job.CompletedAtUtc,
            ExpireAtUtc = job.ExpireAtUtc,
            RowCount = job.RowCount,
            FileSizeBytes = job.FileSizeBytes,
            FailureReason = job.FailureReason,
            FileName = job.FileName,
            ContentType = job.ContentType,
            IsReadyForDownload = string.Equals(job.Status, IdentityUserJobStatuses.Completed, StringComparison.Ordinal) &&
                                 job.FileContent is { Length: > 0 }
        };
    }

    internal static UserImportJobDto MapImportJob(IdentityUserImportJob job)
    {
        var errors = string.IsNullOrWhiteSpace(job.ErrorSnapshotJson)
            ? []
            : JsonSerializer.Deserialize<List<UserImportValidationErrorDto>>(job.ErrorSnapshotJson, JsonOptions) ?? [];

        return new UserImportJobDto
        {
            Id = job.Id,
            TenantId = job.TenantId,
            Status = job.Status,
            FileName = job.FileName,
            RequestedAtUtc = job.RequestedAtUtc,
            StartedAtUtc = job.StartedAtUtc,
            CompletedAtUtc = job.CompletedAtUtc,
            TotalRowCount = job.TotalRowCount,
            CreatedCount = job.CreatedCount,
            FailureReason = job.FailureReason,
            Errors = errors
        };
    }

    internal static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private async Task<UserImportPreparedResult> PrepareImportAsync(string? fileName, string? csvContent)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();

        if (string.IsNullOrWhiteSpace(csvContent))
        {
            throw new SharedValidationException("CsvContent is required.");
        }
        if (csvContent.Length > MaxImportCsvLength)
        {
            throw new SharedValidationException($"CSV content exceeds the {MaxImportCsvLength} character limit.");
        }

        var rows = ParseCsv(csvContent);
        var validation = new UserImportValidationDto
        {
            TotalRowCount = rows.Count
        };

        var preparedRows = new List<UserImportValidatedRow>(rows.Count);
        var seenUserNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedUserNames = rows
            .Select(row => row.Values.GetValueOrDefault("UserName")?.Trim()?.ToUpperInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var normalizedEmails = rows
            .Select(row => row.Values.GetValueOrDefault("Email")?.Trim()?.ToUpperInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var existingUserNames = (await unitOfWorkDbContext.Users
                .AsNoTracking()
                .Where(user =>
                    user.TenantId == tenantId &&
                    user.NormalizedUserName != null &&
                    normalizedUserNames.Contains(user.NormalizedUserName))
                .Select(user => user.NormalizedUserName!)
                .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);
        var existingEmails = (await unitOfWorkDbContext.Users
                .AsNoTracking()
                .Where(user =>
                    user.TenantId == tenantId &&
                    user.NormalizedEmail != null &&
                    normalizedEmails.Contains(user.NormalizedEmail))
                .Select(user => user.NormalizedEmail!)
                .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var userName = row.Values.GetValueOrDefault("UserName")?.Trim();
            var email = row.Values.GetValueOrDefault("Email")?.Trim();
            var password = row.Values.GetValueOrDefault("Password")?.Trim();
            var phoneNumber = NormalizeOptional(row.Values.GetValueOrDefault("PhoneNumber"));
            var roleNames = ParseRoleNames(row.Values.GetValueOrDefault("RoleNames"));
            var isActive = ParseOptionalBool(row.Values.GetValueOrDefault("IsActive"), defaultValue: true, row.RowNumber, validation.Errors);

            if (string.IsNullOrWhiteSpace(userName))
            {
                validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = "UserName is required." });
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = "Email is required." });
            }
            else if (!email.Contains('@', StringComparison.Ordinal))
            {
                validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = "Email is invalid." });
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = "Password is required." });
            }

            if (!string.IsNullOrWhiteSpace(userName) && !seenUserNames.Add(userName))
            {
                validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = "Duplicate UserName exists in the import file." });
            }

            if (!string.IsNullOrWhiteSpace(email) && !seenEmails.Add(email))
            {
                validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = "Duplicate Email exists in the import file." });
            }

            IReadOnlyList<string> normalizedRoles = [];
            try
            {
                normalizedRoles = _managementGuard.NormalizeRequestedRoleNames(roleNames);
                _managementGuard.EnsureCanAssignRoles(normalizedRoles);
            }
            catch (Exception ex) when (ex is SharedValidationException or UnauthorizedAccessException)
            {
                validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = ex.Message });
            }

            if (!string.IsNullOrWhiteSpace(userName))
            {
                var normalizedUserName = userName.ToUpperInvariant();
                if (existingUserNames.Contains(normalizedUserName))
                {
                    validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = "UserName already exists in the current tenant." });
                }
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                var normalizedEmail = email.ToUpperInvariant();
                if (existingEmails.Contains(normalizedEmail))
                {
                    validation.Errors.Add(new UserImportValidationErrorDto { RowNumber = row.RowNumber, Message = "Email already exists in the current tenant." });
                }
            }

            if (validation.Errors.Any(x => x.RowNumber == row.RowNumber))
            {
                continue;
            }

            preparedRows.Add(new UserImportValidatedRow
            {
                RowNumber = row.RowNumber,
                UserName = userName!,
                Email = email!,
                Password = password!,
                PhoneNumber = phoneNumber,
                IsActive = isActive,
                RoleNames = normalizedRoles.ToList()
            });
        }

        validation.ValidRowCount = preparedRows.Count;
        return new UserImportPreparedResult
        {
            Validation = validation,
            Payload = new UserImportPreparedPayload
            {
                FileName = NormalizeOptional(fileName),
                Rows = preparedRows
            }
        };
    }

    private static List<CsvRow> ParseCsv(string csvContent)
    {
        using var reader = new StringReader(csvContent);
        using var parser = new TextFieldParser(reader)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true
        };
        parser.SetDelimiters(",");

        if (parser.EndOfData)
        {
            throw new SharedValidationException("CSV content is empty.");
        }

        var headers = parser.ReadFields()?.Select(x => x?.Trim() ?? string.Empty).ToList()
            ?? throw new SharedValidationException("CSV header is invalid.");

        var requiredHeaders = new[] { "UserName", "Email", "Password" };
        foreach (var header in requiredHeaders)
        {
            if (!headers.Contains(header, StringComparer.OrdinalIgnoreCase))
            {
                throw new SharedValidationException($"CSV header must contain '{header}'.");
            }
        }

        var rows = new List<CsvRow>();
        var rowNumber = 1;
        while (!parser.EndOfData)
        {
            rowNumber++;
            var fields = parser.ReadFields();
            if (fields == null || fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            if (rows.Count >= MaxImportRowCount)
            {
                throw new SharedValidationException($"CSV imports are limited to {MaxImportRowCount} rows.");
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
            {
                values[headers[i]] = i < fields.Length ? fields[i] : null;
            }

            rows.Add(new CsvRow
            {
                RowNumber = rowNumber,
                Values = values
            });
        }

        return rows;
    }

    private static string[] ParseRoleNames(string? value)
    {
        return (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool ParseOptionalBool(
        string? value,
        bool defaultValue,
        int rowNumber,
        List<UserImportValidationErrorDto> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        errors.Add(new UserImportValidationErrorDto
        {
            RowNumber = rowNumber,
            Message = "IsActive must be true or false."
        });
        return defaultValue;
    }

    private static string CreateDownloadToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static BackgroundJobEnqueueOptions CreateEnqueueOptions(Guid? tenantId, Guid requestedByUserId)
    {
        return new BackgroundJobEnqueueOptions
        {
            Queue = IdentityDefaultQueue,
            TenantId = tenantId,
            RequestedByUserId = requestedByUserId
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeFailureReason(string message)
    {
        var normalized = NormalizeOptional(message);
        if (normalized == null)
        {
            return null;
        }

        return normalized.Length <= 512 ? normalized : normalized[..512];
    }

    internal sealed class CsvRow
    {
        public int RowNumber { get; init; }
        public Dictionary<string, string?> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
