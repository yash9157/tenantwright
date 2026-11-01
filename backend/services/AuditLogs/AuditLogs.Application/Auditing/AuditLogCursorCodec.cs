using System.Globalization;
using System.Text;
using AuditLogs.Domain.Auditing;
using Shared;

namespace AuditLogs.Application.Auditing;

internal readonly record struct AuditLogPageCursor(DateTime TimestampUtc, long Id);

internal static class AuditLogCursorCodec
{
    public static string Encode(DateTime timestampUtc, long id)
    {
        var normalized = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : timestampUtc.ToUniversalTime();
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{normalized.Ticks}:{id}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static AuditLogPageCursor Decode(string cursor)
    {
        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var parts = payload.Split(':', 2);
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                ticks <= 0 ||
                id <= 0)
            {
                throw new FormatException();
            }

            return new AuditLogPageCursor(new DateTime(ticks, DateTimeKind.Utc), id);
        }
        catch (Exception exception) when (
            exception is FormatException or
            ArgumentException)
        {
            throw new SharedValidationException("The audit log cursor is invalid.");
        }
    }

    public static IQueryable<AuditLog> ApplyAuditLogCursor(
        IQueryable<AuditLog> query,
        string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return query;
        }

        var decoded = Decode(cursor);
        return query.Where(row =>
            row.ExecutionTime < decoded.TimestampUtc ||
            row.ExecutionTime == decoded.TimestampUtc && row.Id < decoded.Id);
    }

    public static IQueryable<EntityChange> ApplyEntityChangeCursor(
        IQueryable<EntityChange> query,
        string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return query;
        }

        var decoded = Decode(cursor);
        return query.Where(row =>
            row.ChangeTime < decoded.TimestampUtc ||
            row.ChangeTime == decoded.TimestampUtc && row.Id < decoded.Id);
    }
}
