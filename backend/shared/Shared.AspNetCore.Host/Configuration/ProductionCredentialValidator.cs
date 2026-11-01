using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace Shared.AspNetCore.Host.Configuration;

public static class ProductionCredentialValidator
{
    private const string PostgreSqlRuntimeSection = "PostgreSql:Runtime";

    private static readonly string[] PlaceholderValues =
    [
        "change_me",
        "changeme",
        "password",
        "1234"
    ];

    public static void Validate(IConfiguration configuration, bool isDevelopment, string serviceName)
    {
        if (isDevelopment)
        {
            return;
        }

        var defaultConnectionString = configuration.GetConnectionString("Default");
        if (ConnectionStringContainsPlaceholderPassword(defaultConnectionString))
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: serviceName,
                key: "ConnectionStrings:Default",
                rule: "Production connection strings must not contain placeholder credentials.",
                fixHint: "Replace local development credentials with secret-backed production credentials.");
        }

        ValidatePostgreSqlRuntime(configuration, serviceName, defaultConnectionString);

        var rabbitPassword = configuration["Messaging:RabbitMq:Password"];
        if (IsPlaceholderSecret(rabbitPassword))
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: serviceName,
                key: "Messaging:RabbitMq:Password",
                rule: "Production RabbitMQ password must not be a placeholder.",
                fixHint: "Set Messaging__RabbitMq__Password from a Kubernetes secret or equivalent secret store.");
        }

        RejectFalseSecuritySwitch(configuration, serviceName, "SessionValidation:Strict");
        RejectFalseSecuritySwitch(configuration, serviceName, "AuditLogging:Signatures:RequireSignatures");
        RejectFalseSecuritySwitch(configuration, serviceName, "TenantConnectionStrings:Protection:RequireProtectedValues");
    }

    private static void RejectFalseSecuritySwitch(
        IConfiguration configuration,
        string serviceName,
        string key)
    {
        var configured = configuration[key];
        if (configured != null && bool.TryParse(configured, out var enabled) && !enabled)
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: serviceName,
                key: key,
                rule: "Security-critical integrity and revocation controls cannot be disabled outside Development.",
                fixHint: $"Set {key.Replace(':', '_')} to true and redeploy.");
        }
    }

    private static void ValidatePostgreSqlRuntime(
        IConfiguration configuration,
        string serviceName,
        string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) ||
            !configuration.GetValue<bool>($"{PostgreSqlRuntimeSection}:RequireExplicitConfiguration"))
        {
            return;
        }

        var mode = configuration[$"{PostgreSqlRuntimeSection}:Mode"];
        if (mode is not ("Direct" or "TransactionPool" or "SessionPool"))
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                "Mode",
                "Mode must be Direct, TransactionPool, or SessionPool.");
        }

        if (mode == "TransactionPool" &&
            configuration.GetValue<bool?>("EfCore:AutoMigrate") == true)
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                "Mode|EfCore:AutoMigrate",
                "TransactionPool cannot be combined with runtime EF schema migration.",
                "Run migrations through the controlled direct PostgreSQL owner and keep EfCore__AutoMigrate=false.");
        }

        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                "ConnectionStrings:Default",
                "The runtime connection string must use Npgsql key/value syntax.",
                "Store the rotated pooled Npgsql connection string in the service database Secret.");
        }

        ValidateBooleanSetting(
            configuration,
            serviceName,
            builder,
            configurationKey: "ClientPool:Pooling",
            connectionStringKeys: ["Pooling"]);
        ValidateIntegerSetting(
            configuration,
            serviceName,
            builder,
            configurationKey: "ClientPool:MaximumPoolSize",
            minimum: 1,
            maximum: 1000,
            connectionStringKeys: ["Maximum Pool Size", "MaxPoolSize"]);
        ValidateIntegerSetting(
            configuration,
            serviceName,
            builder,
            configurationKey: "ClientPool:MinimumPoolSize",
            minimum: 0,
            maximum: 1000,
            connectionStringKeys: ["Minimum Pool Size", "MinPoolSize"]);
        ValidateIntegerSetting(
            configuration,
            serviceName,
            builder,
            configurationKey: "ClientPool:ConnectionIdleLifetimeSeconds",
            minimum: 1,
            maximum: 3600,
            connectionStringKeys: ["Connection Idle Lifetime"]);
        ValidateIntegerSetting(
            configuration,
            serviceName,
            builder,
            configurationKey: "ClientPool:ConnectionPruningIntervalSeconds",
            minimum: 1,
            maximum: 3600,
            connectionStringKeys: ["Connection Pruning Interval"]);
        ValidateIntegerSetting(
            configuration,
            serviceName,
            builder,
            configurationKey: "ClientPool:ConnectionLifetimeSeconds",
            minimum: 1,
            maximum: 86400,
            connectionStringKeys: ["Connection Lifetime"]);
        ValidateIntegerSetting(
            configuration,
            serviceName,
            builder,
            configurationKey: "ClientPool:ConnectionTimeoutSeconds",
            minimum: 1,
            maximum: 300,
            connectionStringKeys: ["Timeout", "Connection Timeout"]);
        ValidateIntegerSetting(
            configuration,
            serviceName,
            builder,
            configurationKey: "ClientPool:CommandTimeoutSeconds",
            minimum: 1,
            maximum: 600,
            connectionStringKeys: ["Command Timeout"]);

        var minimumPoolSize = GetRequiredInteger(
            configuration,
            serviceName,
            "ClientPool:MinimumPoolSize",
            minimum: 0,
            maximum: 1000);
        var maximumPoolSize = GetRequiredInteger(
            configuration,
            serviceName,
            "ClientPool:MaximumPoolSize",
            minimum: 1,
            maximum: 1000);
        if (minimumPoolSize > maximumPoolSize)
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                "ClientPool:MinimumPoolSize|ClientPool:MaximumPoolSize",
                "MinimumPoolSize cannot exceed MaximumPoolSize.");
        }

        var backendPoolSize = GetRequiredInteger(
            configuration,
            serviceName,
            "BackendPoolSize",
            minimum: mode == "Direct" ? 0 : 1,
            maximum: 1000);
        if (mode == "Direct" && backendPoolSize != 0)
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                "Mode|BackendPoolSize",
                "Direct mode must declare BackendPoolSize=0 because no PgBouncer backend pool is owned.");
        }
    }

    private static void ValidateBooleanSetting(
        IConfiguration configuration,
        string serviceName,
        DbConnectionStringBuilder builder,
        string configurationKey,
        string[] connectionStringKeys)
    {
        var expectedValue = configuration.GetValue<bool?>(
            $"{PostgreSqlRuntimeSection}:{configurationKey}");
        if (!expectedValue.HasValue)
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                configurationKey,
                $"{configurationKey} is required.");
        }

        var actualValue = GetConnectionStringValue(builder, connectionStringKeys);
        if (!bool.TryParse(actualValue, out var parsedValue) || parsedValue != expectedValue.Value)
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                $"{configurationKey}|ConnectionStrings:Default",
                $"The connection string must explicitly match {configurationKey}={expectedValue.Value}.");
        }
    }

    private static void ValidateIntegerSetting(
        IConfiguration configuration,
        string serviceName,
        DbConnectionStringBuilder builder,
        string configurationKey,
        int minimum,
        int maximum,
        string[] connectionStringKeys)
    {
        var expectedValue = GetRequiredInteger(
            configuration,
            serviceName,
            configurationKey,
            minimum,
            maximum);
        var actualValue = GetConnectionStringValue(builder, connectionStringKeys);

        if (!int.TryParse(actualValue, out var parsedValue) || parsedValue != expectedValue)
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                $"{configurationKey}|ConnectionStrings:Default",
                $"The connection string must explicitly match {configurationKey}={expectedValue}.");
        }
    }

    private static int GetRequiredInteger(
        IConfiguration configuration,
        string serviceName,
        string configurationKey,
        int minimum,
        int maximum)
    {
        var value = configuration[$"{PostgreSqlRuntimeSection}:{configurationKey}"];
        if (!int.TryParse(value, out var parsedValue) ||
            parsedValue < minimum ||
            parsedValue > maximum)
        {
            throw InvalidPostgreSqlConfiguration(
                serviceName,
                configurationKey,
                $"{configurationKey} must be an integer between {minimum} and {maximum}.");
        }

        return parsedValue;
    }

    private static string? GetConnectionStringValue(
        DbConnectionStringBuilder builder,
        IEnumerable<string> keys)
    {
        foreach (string builderKey in builder.Keys)
        {
            if (keys.Any(key => string.Equals(key, builderKey, StringComparison.OrdinalIgnoreCase)))
            {
                return builder[builderKey]?.ToString();
            }
        }

        return null;
    }

    private static InvalidOperationException InvalidPostgreSqlConfiguration(
        string serviceName,
        string key,
        string rule,
        string? fixHint = null)
    {
        return StartupConfigExceptionFactory.InvalidConfiguration(
            service: serviceName,
            key: $"{PostgreSqlRuntimeSection}:{key}",
            rule: rule,
            fixHint: fixHint ??
                "Align the chart PostgreSQL runtime settings with the secret-backed Npgsql connection string.");
    }

    private static bool ConnectionStringContainsPlaceholderPassword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = part[..separatorIndex].Trim();
            if (!string.Equals(key, "Password", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "Pwd", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsPlaceholderSecret(part[(separatorIndex + 1)..]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlaceholderSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return PlaceholderValues.Any(placeholder =>
            string.Equals(normalized, placeholder, StringComparison.OrdinalIgnoreCase));
    }
}
