using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// What a dedicated tenant database holds relative to one DbContext's compiled migration lineage.
/// </summary>
/// <param name="PendingMigrationCount">Compiled migrations of the DbContext not recorded as applied.</param>
/// <param name="ForeignMigrationCount">Recorded migrations that do not belong to the DbContext's lineage.</param>
/// <param name="UnmanagedRelationCount">
/// Tables or views present although none of the DbContext's migrations was ever applied: schema some other owner created.
/// </param>
public sealed record TenantDatabaseSchemaState(
    int PendingMigrationCount,
    int ForeignMigrationCount,
    int UnmanagedRelationCount)
{
    /// <summary>
    /// The database holds schema this DbContext did not create, so migrating it would collide with that schema (and share
    /// one migration history with another service). Never migrated automatically.
    /// </summary>
    public bool HoldsForeignSchema => ForeignMigrationCount > 0 || UnmanagedRelationCount > 0;
}

/// <summary>
/// Reads a dedicated database's catalog and migration history without side effects: no DDL, no history-table creation,
/// and no failing probe of a history table that does not exist (EF's own probe logs a failed command on an empty
/// database). PostgreSQL only, like every dedicated tenant database.
/// </summary>
internal static class TenantDatabaseSchemaInspector
{
    private const string RelationsSql = """
        SELECT n.nspname, c.relname, n.nspname = current_schema()
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
          AND n.nspname <> 'information_schema'
          AND n.nspname NOT LIKE 'pg\_%'
        """;

    public static async Task<TenantDatabaseSchemaState> InspectAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var compiled = dbContext.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if (compiled.Count == 0)
        {
            // A DbContext without a migration lineage manages no schema here: nothing to apply, no ownership to judge.
            return new TenantDatabaseSchemaState(0, 0, 0);
        }

        var (historySchema, historyTable) = ResolveHistoryTable(dbContext);
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            var relations = await ReadRelationsAsync(connection, dbContext.Database.CurrentTransaction, cancellationToken);
            var history = relations.FirstOrDefault(relation =>
                string.Equals(relation.Name, historyTable, StringComparison.Ordinal) &&
                (historySchema is null
                    ? relation.IsCurrentSchema
                    : string.Equals(relation.Schema, historySchema, StringComparison.Ordinal)));
            var applied = history is null
                ? []
                : await ReadAppliedMigrationsAsync(
                    connection, dbContext.Database.CurrentTransaction, history, cancellationToken);

            var ownApplied = applied.Count(compiled.Contains);
            var foreign = applied.Count - ownApplied;
            var unmanaged = ownApplied == 0
                ? relations.Count(relation => relation != history)
                : 0;
            return new TenantDatabaseSchemaState(compiled.Count - ownApplied, foreign, unmanaged);
        }
        finally
        {
            if (openedHere)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static (string? Schema, string Table) ResolveHistoryTable(DbContext dbContext)
    {
        var relational = RelationalOptionsExtension.Extract(dbContext.GetService<IDbContextOptions>());
        return (
            relational.MigrationsHistoryTableSchema ?? dbContext.Model.GetDefaultSchema(),
            relational.MigrationsHistoryTableName ?? HistoryRepository.DefaultTableName);
    }

    private static async Task<List<CatalogRelation>> ReadRelationsAsync(
        DbConnection connection,
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = RelationsSql;
        command.Transaction = transaction?.GetDbTransaction();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var relations = new List<CatalogRelation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            relations.Add(new CatalogRelation(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return relations;
    }

    private static async Task<List<string>> ReadAppliedMigrationsAsync(
        DbConnection connection,
        IDbContextTransaction? transaction,
        CatalogRelation history,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // Identifiers come from the catalog row just read, quoted; no caller value reaches the statement.
        command.CommandText =
            $"SELECT \"MigrationId\" FROM {QuoteIdentifier(history.Schema)}.{QuoteIdentifier(history.Name)}";
        command.Transaction = transaction?.GetDbTransaction();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var applied = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record CatalogRelation(string Schema, string Name, bool IsCurrentSchema);
}
