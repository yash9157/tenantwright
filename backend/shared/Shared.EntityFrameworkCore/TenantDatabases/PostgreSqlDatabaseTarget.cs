using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// The physical database a PostgreSQL connection string points at, normalized to (host, port, database) only.
/// Credentials, pooling and every other keyword are deliberately ignored, so two connection strings that differ
/// only in user or options compare as the same target. The <see cref="Fingerprint"/> is the only form that may be
/// logged, stored for comparison, or carried on a message; the target itself is never logged.
/// </summary>
/// <remarks>
/// Normalization is static and conservative: it can only make more spellings collide, never fewer. Host names are
/// compared case-insensitively without a trailing dot; every loopback spelling (<c>localhost</c>, <c>*.localhost</c>,
/// <c>127.0.0.0/8</c>, <c>::1</c> in any notation) is one host; IP literals use their canonical form; an omitted port
/// is PostgreSQL's 5432; database names compare case-insensitively. No DNS resolution happens, so two different
/// names for one server, or two ports reaching it (a proxy and a mapped port), still differ: those aliases are caught
/// only by the owning service at runtime.
/// </remarks>
public sealed record PostgreSqlDatabaseTarget
{
    private const int DefaultPort = 5432;
    private const string LoopbackHost = "localhost";

    private PostgreSqlDatabaseTarget(string host, string database)
    {
        Host = host;
        Database = database;
        Fingerprint = Hash($"{host}|{database}");
    }

    /// <summary>
    /// The canonical endpoint list: each <c>host:port</c> normalized, de-duplicated and ordinally sorted, joined by
    /// <c>,</c> (multi-host connection strings name one cluster whatever the order).
    /// </summary>
    public string Host { get; }

    /// <summary>The lower-cased database name; PostgreSQL defaults it to the user name when it is omitted.</summary>
    public string Database { get; }

    /// <summary>Stable, non-reversible identifier of the target: lower-case hex of SHA-256("endpoints|database").</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Parses a PostgreSQL connection string. Returns false when the value is not a well-formed Npgsql connection
    /// string or names no host; the value is never echoed.
    /// </summary>
    public static bool TryParse(string? connectionString, [NotNullWhen(true)] out PostgreSqlDatabaseTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }

        var database = string.IsNullOrWhiteSpace(builder.Database)
            ? builder.Username?.Trim()
            : builder.Database.Trim();
        if (string.IsNullOrWhiteSpace(database) ||
            !TryNormalizeEndpoints(builder.Host, builder.Port > 0 ? builder.Port : DefaultPort, out var endpoints))
        {
            return false;
        }

        target = new PostgreSqlDatabaseTarget(endpoints, database.ToLowerInvariant());
        return true;
    }

    /// <summary>
    /// Parses a target given as its parts (configuration that names a database without any credential). The same
    /// normalization as <see cref="TryParse(string?, out PostgreSqlDatabaseTarget?)"/> applies.
    /// </summary>
    public static bool TryCreate(
        string? host,
        int? port,
        string? database,
        [NotNullWhen(true)] out PostgreSqlDatabaseTarget? target)
    {
        target = null;
        var effectivePort = port ?? DefaultPort;
        if (string.IsNullOrWhiteSpace(database) || effectivePort is < 1 or > 65535 ||
            !TryNormalizeEndpoints(host, effectivePort, out var endpoints))
        {
            return false;
        }

        target = new PostgreSqlDatabaseTarget(endpoints, database.Trim().ToLowerInvariant());
        return true;
    }

    /// <summary>
    /// The fingerprint of any connection string: the normalized target fingerprint when it parses as PostgreSQL,
    /// otherwise a hash of the trimmed raw value, so non-PostgreSQL test providers still compare deterministically.
    /// </summary>
    public static string FingerprintOf(string connectionString)
    {
        return TryParse(connectionString, out var target)
            ? target.Fingerprint
            : Hash($"raw|{connectionString.Trim()}");
    }

    /// <summary>
    /// Npgsql's <c>Host</c> is a comma-separated list whose entries may carry their own <c>:port</c> (IPv6 literals in
    /// brackets); entries without one use <paramref name="defaultPort"/>.
    /// </summary>
    private static bool TryNormalizeEndpoints(string? hostList, int defaultPort, [NotNullWhen(true)] out string? endpoints)
    {
        endpoints = null;
        if (string.IsNullOrWhiteSpace(hostList))
        {
            return false;
        }

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in hostList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TrySplitPort(entry, defaultPort, out var host, out var port))
            {
                return false;
            }

            var canonicalHost = CanonicalHost(host);
            if (canonicalHost.Length == 0)
            {
                return false;
            }

            normalized.Add($"{canonicalHost}:{port.ToString(CultureInfo.InvariantCulture)}");
        }

        if (normalized.Count == 0)
        {
            return false;
        }

        endpoints = string.Join(',', normalized);
        return true;
    }

    private static bool TrySplitPort(string entry, int defaultPort, out string host, out int port)
    {
        host = entry;
        port = defaultPort;

        // A Unix-domain socket directory is a path, never host:port.
        if (entry.StartsWith('/'))
        {
            return true;
        }

        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = entry[1..close];
            var rest = entry[(close + 1)..];
            return rest.Length == 0 || (rest[0] == ':' && TryParsePort(rest[1..], out port));
        }

        // More than one colon without brackets is a bare IPv6 literal, which carries no port.
        var colon = entry.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || colon != entry.LastIndexOf(':'))
        {
            return true;
        }

        host = entry[..colon];
        return TryParsePort(entry[(colon + 1)..], out port);
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private static string CanonicalHost(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.StartsWith('/'))
        {
            return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
        }

        var name = trimmed.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(name, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            return IPAddress.IsLoopback(address) ? LoopbackHost : address.ToString();
        }

        return name == LoopbackHost || name.EndsWith(".localhost", StringComparison.Ordinal) ? LoopbackHost : name;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
