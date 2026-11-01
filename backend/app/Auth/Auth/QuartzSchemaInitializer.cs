using System.Data;
using System.Data.Common;
using System.Reflection;
using Auth.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.AspNetCore.Host.Configuration;

namespace Auth;

internal static class QuartzSchemaInitializer
{
    private const string QuartzJobDetailsTable = "qrtz_job_details";
    private const string SchemaResourceName = "Auth.Sql.QuartzPostgresSchema.sql";
    private const long SchemaMigrationLockId = 4083250644312787306;

    private static readonly Lazy<string> SchemaSql = new(LoadSchemaSql, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void ValidateRuntimeConfiguration(
        IConfiguration configuration,
        bool usePersistentStore,
        bool autoCreateSchema)
    {
        if (usePersistentStore &&
            autoCreateSchema &&
            string.Equals(
                configuration["PostgreSql:Runtime:Mode"],
                "TransactionPool",
                StringComparison.Ordinal))
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: "tenantwright-auth",
                key: "PostgreSql:Runtime:Mode|Auth:Quartz:AutoCreateSchema",
                rule: "Quartz schema creation uses a PostgreSQL session advisory lock and cannot run through transaction-mode PgBouncer.",
                fixHint: "Create and verify the Quartz schema through the controlled direct PostgreSQL owner, then set Auth__Quartz__AutoCreateSchema=false before deploying the pooled runtime.");
        }
    }

    public static async Task EnsureCreatedAsync(
        AuthDbContext dbContext,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await ExecuteNonQueryAsync(
                connection,
                $"SELECT pg_advisory_lock({SchemaMigrationLockId});",
                cancellationToken);

            if (await JobDetailsTableExistsAsync(connection, cancellationToken))
            {
                logger.LogInformation("Quartz schema table {Table} already exists.", QuartzJobDetailsTable);
                return;
            }

            logger.LogInformation("Quartz schema table {Table} not found. Creating Quartz schema.", QuartzJobDetailsTable);
            await ExecuteNonQueryAsync(connection, SchemaSql.Value, cancellationToken);
            logger.LogInformation("Quartz schema created successfully.");
        }
        finally
        {
            try
            {
                await ExecuteNonQueryAsync(
                    connection,
                    $"SELECT pg_advisory_unlock({SchemaMigrationLockId});",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release Quartz schema advisory lock.");
            }

            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    public static async Task<bool> IsCreatedAsync(
        AuthDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return await JobDetailsTableExistsAsync(connection, cancellationToken);
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<bool> JobDetailsTableExistsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('qrtz_job_details') IS NOT NULL;";

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is bool value)
        {
            return value;
        }

        return scalar?.ToString() is "t" or "true" or "1";
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandType = CommandType.Text;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string LoadSchemaSql()
    {
        var assembly = typeof(AuthHostModule).Assembly;
        using var stream = assembly.GetManifestResourceStream(SchemaResourceName);
        if (stream == null)
        {
            throw new InvalidOperationException(
                $"Could not find embedded Quartz schema resource '{SchemaResourceName}'.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
