using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Shared.Data.ConnectionStrings;

/// <summary>
/// The single reviewed catalog of the connection-string names TenantWright service DbContexts use, mirroring how
/// <c>TenantWrightMessagingTopology</c> is the reviewed messaging catalog. Each service owns exactly one database name.
/// <para>
/// Host resolution of every name falls back to <c>ConnectionStrings:Default</c>, so a deployment that only
/// configures <c>Default</c> per service keeps working. Tenant resolution consults the tenant's own connection
/// string only for names in <see cref="TenantDedicatable"/>: those services keep every tenant-owned row behind
/// <c>IMultiTenant</c> and run their cross-tenant work per database. Every other name is host-only: its DbContext
/// is either multi-tenancy-unaware (Auth, Identity, Tenant) or owns no tenant data (Workflows), so a tenant can
/// never re-point it.
/// </para>
/// <para>
/// There is deliberately no tenant-level "Default": every service creates the same neutral messaging tables, so
/// one tenant database shared by several services would collide on schema and mix service ownership.
/// </para>
/// </summary>
public static class TenantWrightDatabaseCatalog
{
    public const string AuditLogs = "AuditLogs";
    public const string Auth = "Auth";
    public const string Identity = "Identity";
    public const string Notifications = "Notifications";
    public const string Permissions = "Permissions";
    public const string Tenant = "Tenant";
    public const string Workflows = "Workflows";

    private static readonly FrozenSet<string> KnownNames =
        new[] { AuditLogs, Auth, Identity, Notifications, Permissions, Tenant, Workflows }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> TenantDedicatableNames =
        new[] { AuditLogs, Notifications, Permissions }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every service database name, in ordinal order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [AuditLogs, Auth, Identity, Notifications, Permissions, Tenant, Workflows];

    /// <summary>The service database names a tenant may move to a dedicated database, in ordinal order.</summary>
    public static IReadOnlyList<string> TenantDedicatable { get; } = [AuditLogs, Notifications, Permissions];

    /// <summary>True when <paramref name="name"/> is a catalog name (ordinal, exact match).</summary>
    public static bool IsKnown(string? name) => name != null && KnownNames.Contains(name);

    /// <summary>
    /// True when a tenant may hold a dedicated database for <paramref name="name"/> (ordinal, exact match).
    /// Unknown names are never dedicatable, so an unreviewed DbContext always resolves to the host database.
    /// </summary>
    public static bool SupportsDedicatedTenantDatabase(string? name) =>
        name != null && TenantDedicatableNames.Contains(name);
}
