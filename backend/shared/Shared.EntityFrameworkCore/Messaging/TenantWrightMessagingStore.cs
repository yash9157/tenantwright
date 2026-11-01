using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Shared.EventBus.Messaging;

namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>A row leased by one dispatcher claim.</summary>
internal sealed record ClaimedOutboxMessage(
    Guid Id,
    string ContractIdentity,
    TenantWrightMessageRouteKind Kind,
    string? Destination,
    byte[] Payload,
    IReadOnlyDictionary<string, string> Headers,
    int Attempts,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// The outbox and inbox maintenance PostgreSQL statements. Every statement is a single autocommitted command: no transaction is held open
/// across a broker call, and every state change after a claim is fenced on the claim's unique lease owner.
/// </summary>
internal sealed class TenantWrightMessagingStore(DbContext dbContext)
{
    private const string Table = $"{TenantWrightMessagingTables.Schema}.\"{TenantWrightMessagingTables.Outbox}\"";
    private const string InboxTable = $"{TenantWrightMessagingTables.Schema}.\"{TenantWrightMessagingTables.Inbox}\"";

    /// <summary>
    /// Leases up to <paramref name="batchSize"/> due rows with one statement. <c>FOR UPDATE SKIP LOCKED</c> keeps
    /// concurrent claims disjoint; the lease keeps them disjoint after the statement commits. The attempt is counted at
    /// claim time, so a dispatcher that crashes mid-dispatch still consumes an attempt.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        string leaseOwner,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync($"""
            WITH due AS (
                SELECT "Id"
                FROM {Table}
                WHERE "DispatchedAtUtc" IS NULL
                  AND "ParkedAtUtc" IS NULL
                  AND "AvailableAtUtc" <= @now
                  AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= @now)
                ORDER BY "AvailableAtUtc", "Id"
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
            )
            UPDATE {Table} AS message
            SET "LockedUntilUtc" = @leaseUntil,
                "LockedBy" = @owner,
                "Attempts" = message."Attempts" + 1
            FROM due
            WHERE message."Id" = due."Id"
            RETURNING message."Id", message."ContractIdentity", message."Kind", message."Destination",
                      message."Payload", message."Headers"::text, message."Attempts", message."CreatedAtUtc"
            """, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", now));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("leaseUntil", leaseUntil));
        command.Parameters.Add(new NpgsqlParameter<string>("owner", leaseOwner));
        command.Parameters.Add(new NpgsqlParameter<int>("batch", batchSize));

        var claimed = new List<ClaimedOutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedOutboxMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                (TenantWrightMessageRouteKind)reader.GetInt16(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<byte[]>(4),
                JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5))
                    ?? throw new MessagingSecurityException(MessagingSecurityException.InvalidContext),
                reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        // Claim order is not guaranteed by RETURNING; dispatch in availability order within the batch.
        return claimed.OrderBy(message => message.CreatedAtUtc).ThenBy(message => message.Id).ToArray();
    }

    public Task<bool> MarkDispatchedAsync(Guid id, string leaseOwner, DateTimeOffset dispatchedAt, CancellationToken cancellationToken) =>
        ExecuteFencedAsync($"""
            UPDATE {Table}
            SET "DispatchedAtUtc" = @at, "LockedUntilUtc" = NULL, "LockedBy" = NULL, "LastErrorType" = NULL
            WHERE "Id" = @id AND "LockedBy" = @owner AND "DispatchedAtUtc" IS NULL
            """, id, leaseOwner, dispatchedAt, errorType: null, cancellationToken);

    public Task<bool> ScheduleRetryAsync(
        Guid id,
        string leaseOwner,
        DateTimeOffset availableAt,
        string errorType,
        CancellationToken cancellationToken) =>
        ExecuteFencedAsync($"""
            UPDATE {Table}
            SET "AvailableAtUtc" = @at, "LockedUntilUtc" = NULL, "LockedBy" = NULL, "LastErrorType" = @error
            WHERE "Id" = @id AND "LockedBy" = @owner AND "DispatchedAtUtc" IS NULL
            """, id, leaseOwner, availableAt, errorType, cancellationToken);

    public Task<bool> ParkAsync(
        Guid id,
        string leaseOwner,
        DateTimeOffset parkedAt,
        string errorType,
        CancellationToken cancellationToken) =>
        ExecuteFencedAsync($"""
            UPDATE {Table}
            SET "ParkedAtUtc" = @at, "LockedUntilUtc" = NULL, "LockedBy" = NULL, "LastErrorType" = @error
            WHERE "Id" = @id AND "LockedBy" = @owner AND "DispatchedAtUtc" IS NULL
            """, id, leaseOwner, parkedAt, errorType, cancellationToken);

    /// <summary>Returns leased rows that were never attempted, refunding the attempt the claim counted.</summary>
    public async Task ReleaseUnattemptedAsync(
        IReadOnlyCollection<Guid> ids,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;

        await using var command = await CreateCommandAsync($"""
            UPDATE {Table}
            SET "LockedUntilUtc" = NULL, "LockedBy" = NULL, "Attempts" = GREATEST("Attempts" - 1, 0)
            WHERE "Id" = ANY(@ids) AND "LockedBy" = @owner AND "DispatchedAtUtc" IS NULL
            """, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.ToArray() });
        command.Parameters.Add(new NpgsqlParameter<string>("owner", leaseOwner));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deletes at most <paramref name="batchSize"/> rows dispatched before <paramref name="cutoff"/>.</summary>
    public async Task<int> DeleteDispatchedBeforeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync($"""
            DELETE FROM {Table}
            WHERE "Id" IN (
                SELECT "Id"
                FROM {Table}
                WHERE "DispatchedAtUtc" IS NOT NULL AND "DispatchedAtUtc" < @cutoff
                ORDER BY "DispatchedAtUtc"
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            """, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));
        command.Parameters.Add(new NpgsqlParameter<int>("batch", batchSize));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deletes at most <paramref name="batchSize"/> inbox records processed before <paramref name="cutoff"/>.</summary>
    public async Task<int> DeleteInboxRecordsBeforeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync($"""
            DELETE FROM {InboxTable}
            WHERE ("MessageId", "Endpoint") IN (
                SELECT "MessageId", "Endpoint"
                FROM {InboxTable}
                WHERE "ProcessedAtUtc" < @cutoff
                ORDER BY "ProcessedAtUtc"
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            """, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));
        command.Parameters.Add(new NpgsqlParameter<int>("batch", batchSize));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> ExecuteFencedAsync(
        string sql,
        Guid id,
        string leaseOwner,
        DateTimeOffset at,
        string? errorType,
        CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        command.Parameters.Add(new NpgsqlParameter<string>("owner", leaseOwner));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("at", at));
        if (errorType is not null)
            command.Parameters.Add(new NpgsqlParameter<string>("error", Truncate(errorType)));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<NpgsqlCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        if (dbContext.Database.GetDbConnection() is not NpgsqlConnection connection)
            throw new InvalidOperationException("The TenantWright outbox requires a PostgreSQL (Npgsql) DbContext.");
        if (dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Outbox dispatch statements must not run inside a caller's transaction.");
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        return new NpgsqlCommand(sql, connection);
    }

    private static string Truncate(string value) =>
        value.Length <= TenantWrightMessagingTables.ErrorTypeMaxLength
            ? value
            : value[..TenantWrightMessagingTables.ErrorTypeMaxLength];
}
