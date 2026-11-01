using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Tenant.Application.Tenants;

public static class TenantSharedDatabaseTargetsRegistration
{
    /// <summary>
    /// Binds <see cref="TenantSharedDatabaseTargetsOptions"/> with unknown keys rejected (so no credential can be
    /// configured beside a target) and validates it at startup.
    /// </summary>
    public static IServiceCollection AddTenantSharedDatabaseTargets(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<IValidateOptions<TenantSharedDatabaseTargetsOptions>, TenantSharedDatabaseTargetsOptionsValidator>();
        services.AddOptions<TenantSharedDatabaseTargetsOptions>()
            .Bind(
                configuration.GetSection(TenantSharedDatabaseTargetsOptions.SectionName),
                binder => binder.ErrorOnUnknownConfiguration = true)
            .ValidateOnStart();
        return services;
    }
}

/// <summary>
/// The shared databases of the services tenants may move to a dedicated database, as targets only (host, port,
/// database). The Tenant host has no connection to them and receives no credential for them; it needs only enough to
/// refuse a dedicated assignment that would point a tenant at another service's shared database.
/// </summary>
/// <remarks>
/// Bound from <c>TenantDatabases:SharedTargets:{AuditLogs|Notifications|Permissions}</c> with unknown keys rejected, so a
/// <c>Password</c>, <c>Username</c> or <c>ConnectionString</c> key fails startup instead of being silently held.
/// </remarks>
public sealed class TenantSharedDatabaseTargetsOptions
{
    public const string SectionName = "TenantDatabases";

    /// <summary>Keyed by <see cref="TenantWrightDatabaseCatalog.TenantDedicatable"/> names.</summary>
    public Dictionary<string, TenantSharedDatabaseTarget> SharedTargets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The normalized fingerprints of every configured target; call only after validation.</summary>
    public IEnumerable<string> Fingerprints() =>
        SharedTargets.Values.Select(target =>
            PostgreSqlDatabaseTarget.TryCreate(target.Host, target.Port, target.Database, out var parsed)
                ? parsed.Fingerprint
                : throw new InvalidOperationException("Shared database targets were used before validation."));
}

/// <summary>One shared service database, named without any credential.</summary>
public sealed class TenantSharedDatabaseTarget
{
    public string? Host { get; set; }

    /// <summary>Defaults to PostgreSQL's 5432 when omitted.</summary>
    public int? Port { get; set; }

    public string? Database { get; set; }
}

public sealed class TenantSharedDatabaseTargetsOptionsValidator : IValidateOptions<TenantSharedDatabaseTargetsOptions>
{
    public ValidateOptionsResult Validate(string? name, TenantSharedDatabaseTargetsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        foreach (var (serviceName, target) in options.SharedTargets)
        {
            if (!TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(serviceName))
            {
                failures.Add(
                    $"TenantDatabases:SharedTargets:{serviceName} is not a dedicatable database; use one of " +
                    $"{string.Join(", ", TenantWrightDatabaseCatalog.TenantDedicatable)}.");
                continue;
            }

            if (target is null || !PostgreSqlDatabaseTarget.TryCreate(target.Host, target.Port, target.Database, out _))
            {
                failures.Add(
                    $"TenantDatabases:SharedTargets:{serviceName} requires Host, Database and an optional Port between 1 and 65535.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
