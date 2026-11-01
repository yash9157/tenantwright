using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Data.ConnectionStrings;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Tenant.Application.Contracts.Tenants;
using Tenant.Domain.Shared;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

/// <inheritdoc cref="ITenantDatabaseAppService"/>
/// <remarks>
/// A dedicated target is rejected when it is a shared service database — every connection string configured for the
/// Tenant host, plus the shared database of each dedicatable service named in
/// <see cref="TenantSharedDatabaseTargetsOptions"/> (targets only, no credentials) — or when any other assignment
/// (another tenant, or another name of the same tenant) already points at it. Targets are compared on their normalized
/// (host, port, database) fingerprint (<see cref="PostgreSqlDatabaseTarget"/>), so case, trailing dots, loopback
/// spellings and an omitted default port cannot disguise one. Detection here is best effort for routes it cannot see
/// statically (a second DNS name, a proxy port): the owning service's runtime check that a tenant's target is not its
/// own shared database, and the migrator's refusal of a database holding another service's schema, are the
/// authoritative back-stops. A unique index on the stored target fingerprint closes the race between concurrent
/// assignments.
/// After commit the tenant cache is refreshed and <see cref="TenantDatabaseChangedEto"/> (written to the outbox in the
/// same transaction) tells the owning service to migrate and seed the database.
/// </remarks>
public class TenantDatabaseAppService : ITenantDatabaseAppService, ITransientDependency, IUnitOfWorkEnabled
{
    private static readonly TimeSpan ConnectionTestTimeout = TimeSpan.FromSeconds(5);

    private readonly IRepository<Domain.Tenants.Tenant, Guid> _tenantRepository;
    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;
    private readonly TenantCacheManager _tenantCacheManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly ITenantWrightMessageSender _messages;
    private readonly ITenantConnectionStringProtector _connectionStringProtector;
    private readonly IOptions<TenantConnectionStringProtectionOptions> _protectionOptions;
    private readonly IOptionsMonitor<SharedDbConnectionOptions> _hostConnectionOptions;
    private readonly IOptions<TenantSharedDatabaseTargetsOptions> _sharedTargets;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ILogger<TenantDatabaseAppService> _logger;

    public TenantDatabaseAppService(
        IRepository<Domain.Tenants.Tenant, Guid> tenantRepository,
        IDbContextProvider<TenantDbContextApp> dbContextProvider,
        TenantCacheManager tenantCacheManager,
        ICurrentTenant currentTenant,
        ITenantWrightMessageSender messages,
        ITenantConnectionStringProtector connectionStringProtector,
        IOptions<TenantConnectionStringProtectionOptions> protectionOptions,
        IOptionsMonitor<SharedDbConnectionOptions> hostConnectionOptions,
        IOptions<TenantSharedDatabaseTargetsOptions> sharedTargets,
        IUnitOfWorkManager unitOfWorkManager,
        ILogger<TenantDatabaseAppService> logger)
    {
        _tenantRepository = tenantRepository;
        _dbContextProvider = dbContextProvider;
        _tenantCacheManager = tenantCacheManager;
        _currentTenant = currentTenant;
        _messages = messages;
        _connectionStringProtector = connectionStringProtector;
        _protectionOptions = protectionOptions;
        _hostConnectionOptions = hostConnectionOptions;
        _sharedTargets = sharedTargets;
        _unitOfWorkManager = unitOfWorkManager;
        _logger = logger;
    }

    public async Task<TenantDatabaseListDto> GetDatabasesAsync(Guid id)
    {
        EnsureHost();
        var tenant = await GetTenantAsync(id);
        return new TenantDatabaseListDto
        {
            Items = TenantDatabaseStatusMapper.Map(tenant),
            ConcurrencyStamp = tenant.ConcurrencyStamp
        };
    }

    [Audited]
    public async Task<TenantDatabaseTestResultDto> TestDatabaseAsync(Guid id, string name)
    {
        EnsureHost();
        var databaseName = RequireDedicatableName(name);
        var tenant = await GetTenantAsync(id);
        var storedValue = tenant.FindConnectionString(databaseName);
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            throw new SharedValidationException(
                TenantErrorCodes.DedicatedDatabaseNotAssigned,
                "The tenant has no dedicated database for this name.");
        }

        return await TestConnectionAsync(_connectionStringProtector.Unprotect(id, databaseName, storedValue));
    }

    [Audited(Category = "tenant.lifecycle")]
    public async Task<SetTenantDatabaseResultDto> SetDatabaseAsync(Guid id, string name, SetTenantDatabaseInput input)
    {
        EnsureHost();
        ArgumentNullException.ThrowIfNull(input);
        var databaseName = RequireDedicatableName(name);
        var connectionString = input.ConnectionString?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString) ||
            !PostgreSqlDatabaseTarget.TryParse(connectionString, out var target))
        {
            throw new SharedValidationException(
                TenantErrorCodes.DedicatedDatabaseConnectionStringInvalid,
                "The connection string must be a PostgreSQL connection string with a host and a database.");
        }

        var tenant = await GetTenantAsync(id);
        EnsureConcurrency(tenant, input.ConcurrencyStamp);

        var existingValue = tenant.FindConnectionString(databaseName);
        var hasExisting = !string.IsNullOrWhiteSpace(existingValue);

        // Every assignment re-points the tenant away from where its rows live today: a first assignment strands the
        // tenant's rows in the shared database exactly as a change strands them in the previous dedicated one.
        if (!input.AcknowledgeNoDataMigration)
        {
            throw new SharedValidationException(
                TenantErrorCodes.DedicatedDatabaseDataMigrationNotAcknowledged,
                "Assigning a dedicated database does not move existing data. Confirm with AcknowledgeNoDataMigration.");
        }

        await EnsureTargetAvailableAsync(id, databaseName, target.Fingerprint);

        TenantDatabaseTestResultDto? testResult = null;
        if (input.TestBeforeSave)
        {
            testResult = await TestConnectionAsync(connectionString);
            if (!testResult.Success)
            {
                return new SetTenantDatabaseResultDto
                {
                    Status = TenantDatabaseStatusMapper.Map(tenant, databaseName),
                    Change = TenantDatabaseAssignmentChange.NotSaved,
                    TestResult = testResult
                };
            }
        }

        var change = !hasExisting
            ? TenantDatabaseAssignmentChange.Assigned
            : string.Equals(CurrentTargetFingerprint(tenant, databaseName, existingValue!), target.Fingerprint, StringComparison.Ordinal)
                ? TenantDatabaseAssignmentChange.Updated
                : TenantDatabaseAssignmentChange.Reassigned;

        var protection = _protectionOptions.Value;
        tenant.SetProtectedConnectionString(
            databaseName,
            _connectionStringProtector.Protect(id, databaseName, connectionString),
            protection.CurrentKeyId
                ?? throw new InvalidOperationException("Tenant connection-string protection key is not configured."),
            protection.CurrentKeyVersion,
            target.Fingerprint);
        await _tenantRepository.UpdateAsync(tenant, autoSave: _unitOfWorkManager.Current == null);
        await PublishChangedAsync(id, databaseName, ToContract(change), target.Fingerprint);
        await ScheduleCacheUpsertAsync(tenant);

        _logger.LogInformation(
            "Dedicated database {DatabaseName} {Change} for a tenant. TenantHash={TenantHash}.",
            databaseName,
            change,
            TenantResolutionHash.HashTenantId(id));

        return new SetTenantDatabaseResultDto
        {
            Status = TenantDatabaseStatusMapper.Map(tenant, databaseName),
            Change = change,
            DataMigrated = false,
            TestResult = testResult
        };
    }

    [Audited(Category = "tenant.lifecycle")]
    public async Task<TenantDatabaseStatusDto> RemoveDatabaseAsync(Guid id, string name, RemoveTenantDatabaseInput input)
    {
        EnsureHost();
        ArgumentNullException.ThrowIfNull(input);
        var databaseName = RequireDedicatableName(name);
        var tenant = await GetTenantAsync(id);
        EnsureConcurrency(tenant, input.ConcurrencyStamp);

        if (string.IsNullOrWhiteSpace(tenant.FindConnectionString(databaseName)))
        {
            throw new SharedValidationException(
                TenantErrorCodes.DedicatedDatabaseNotAssigned,
                "The tenant has no dedicated database for this name.");
        }

        if (!input.AcknowledgeNoDataMigration)
        {
            throw new SharedValidationException(
                TenantErrorCodes.DedicatedDatabaseDataMigrationNotAcknowledged,
                "Returning to the shared database does not move data. Confirm with AcknowledgeNoDataMigration.");
        }

        tenant.RemoveConnectionString(databaseName);
        await _tenantRepository.UpdateAsync(tenant, autoSave: _unitOfWorkManager.Current == null);
        await PublishChangedAsync(id, databaseName, TenantDatabaseChangeKindEto.Removed, targetFingerprint: null);
        await ScheduleCacheUpsertAsync(tenant);

        _logger.LogInformation(
            "Dedicated database {DatabaseName} removed for a tenant. TenantHash={TenantHash}.",
            databaseName,
            TenantResolutionHash.HashTenantId(id));

        return TenantDatabaseStatusMapper.Map(tenant, databaseName);
    }

    /// <summary>
    /// Rejects a target that is a shared database known to this host (its own connection strings and the configured
    /// shared targets of the dedicatable services) or that any other assignment already uses. Assignments of
    /// soft-deleted tenants count too: reusing a deleted tenant's database would expose its data.
    /// </summary>
    private async Task EnsureTargetAvailableAsync(Guid tenantId, string databaseName, string targetFingerprint)
    {
        var sharedTargets = _hostConnectionOptions.CurrentValue.ConnectionStrings.Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => PostgreSqlDatabaseTarget.FingerprintOf(value!))
            .Concat(_sharedTargets.Value.Fingerprints());
        if (sharedTargets.Contains(targetFingerprint, StringComparer.Ordinal))
        {
            throw TargetInUse();
        }

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var candidates = await dbContext.TenantConnectionStrings
            .AsNoTracking()
            .Where(row => !(row.TenantId == tenantId && row.Name == databaseName) &&
                          (row.TargetFingerprint == targetFingerprint || row.TargetFingerprint == null))
            .Select(row => new { row.TenantId, row.Name, row.TargetFingerprint, row.ProtectedValue, row.Value })
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            var fingerprint = candidate.TargetFingerprint;
            if (fingerprint == null)
            {
                // Rows written before target fingerprints existed: compare on the decrypted target.
                var stored = candidate.ProtectedValue ?? candidate.Value;
                if (string.IsNullOrWhiteSpace(stored))
                {
                    continue;
                }

                fingerprint = PostgreSqlDatabaseTarget.FingerprintOf(
                    _connectionStringProtector.Unprotect(candidate.TenantId, candidate.Name, stored));
            }

            if (string.Equals(fingerprint, targetFingerprint, StringComparison.Ordinal))
            {
                throw TargetInUse();
            }
        }
    }

    private string CurrentTargetFingerprint(Domain.Tenants.Tenant tenant, string databaseName, string storedValue) =>
        tenant.FindConnectionTargetFingerprint(databaseName)
        ?? PostgreSqlDatabaseTarget.FingerprintOf(
            _connectionStringProtector.Unprotect(tenant.Id, databaseName, storedValue));

    private Task PublishChangedAsync(
        Guid tenantId,
        string databaseName,
        TenantDatabaseChangeKindEto changeKind,
        string? targetFingerprint) =>
        _messages.PublishEventAsync(new TenantDatabaseChangedEto
        {
            TenantId = tenantId,
            DatabaseName = databaseName,
            ChangeKind = changeKind,
            TargetFingerprint = targetFingerprint,
            OccurredAtUtc = DateTime.UtcNow
        });

    private Task ScheduleCacheUpsertAsync(Domain.Tenants.Tenant tenant)
    {
        // Completion handlers run after the transaction-owned DbContext committed; cache the materialized aggregate.
        var unitOfWork = _unitOfWorkManager.Current;
        if (unitOfWork == null)
        {
            return _tenantCacheManager.UpsertTenantAsync(tenant);
        }

        unitOfWork.OnCompleted(() => _tenantCacheManager.UpsertTenantAsync(tenant));
        return Task.CompletedTask;
    }

    private async Task<Domain.Tenants.Tenant> GetTenantAsync(Guid id)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return await dbContext.Tenants
                   .Include(x => x.ConnectionStrings)
                   .FirstOrDefaultAsync(x => x.Id == id)
               ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
    }

    private void EnsureHost()
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Tenant database management is restricted to host administrators.");
        }
    }

    private static string RequireDedicatableName(string? name)
    {
        var trimmed = name?.Trim();
        if (!TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(trimmed))
        {
            throw new SharedValidationException(
                TenantErrorCodes.DedicatedDatabaseNameNotSupported,
                $"Dedicated databases are supported only for: {string.Join(", ", TenantWrightDatabaseCatalog.TenantDedicatable)}.");
        }

        return trimmed!;
    }

    private static void EnsureConcurrency(Domain.Tenants.Tenant tenant, string? expectedStamp)
    {
        if (!string.IsNullOrWhiteSpace(expectedStamp) &&
            !string.Equals(tenant.ConcurrencyStamp, expectedStamp, StringComparison.Ordinal))
        {
            throw new SharedConflictException("The tenant was modified by someone else. Reload and try again.");
        }
    }

    private static SharedConflictException TargetInUse() =>
        new(
            TenantErrorCodes.DedicatedDatabaseTargetInUse,
            "The database is a shared service database or is already assigned as another dedicated database.");

    private static TenantDatabaseChangeKindEto ToContract(TenantDatabaseAssignmentChange change) =>
        change switch
        {
            TenantDatabaseAssignmentChange.Assigned => TenantDatabaseChangeKindEto.Assigned,
            TenantDatabaseAssignmentChange.Reassigned => TenantDatabaseChangeKindEto.Reassigned,
            TenantDatabaseAssignmentChange.Updated => TenantDatabaseChangeKindEto.Updated,
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Only saved changes are published.")
        };

    /// <summary>Opens and closes a connection. Reports only the failure type, never the value or driver message.</summary>
    private static async Task<TenantDatabaseTestResultDto> TestConnectionAsync(string connectionString)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(ConnectionTestTimeout);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellation.Token);
            await connection.CloseAsync();
            return new TenantDatabaseTestResultDto { Success = true, Message = "Connection succeeded." };
        }
        catch (OperationCanceledException)
        {
            return new TenantDatabaseTestResultDto { Success = false, Message = "Connection timed out." };
        }
        catch (Exception exception) when (exception is NpgsqlException or ArgumentException or InvalidOperationException
                                              or System.Net.Sockets.SocketException or TimeoutException)
        {
            return new TenantDatabaseTestResultDto
            {
                Success = false,
                Message = $"Connection failed: {exception.GetType().Name}."
            };
        }
    }
}
