using Microsoft.Extensions.Configuration;

namespace Shared.BackgroundTasks.Hangfire;

public sealed class BackgroundTasksHangfireOptions
{
    public const string SectionName = "BackgroundTasks:Hangfire";

    public bool Enabled { get; set; }
    public string? ServiceName { get; set; }
    public string ConnectionStringName { get; set; } = "Default";
    public string? SchemaName { get; set; }
    public bool? PrepareSchemaIfNecessary { get; set; }
    public List<string> Queues { get; set; } = [];
    public int WorkerCount { get; set; }
    public BackgroundTasksHangfireDashboardOptions Dashboard { get; set; } = new();

    public void Validate(IConfiguration configuration)
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            throw new InvalidOperationException("BackgroundTasks:Hangfire:ServiceName is required when Hangfire is enabled.");
        }

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringName)))
        {
            throw new InvalidOperationException(
                $"BackgroundTasks:Hangfire requires connection string '{ConnectionStringName}'.");
        }

        if (Dashboard.Enabled &&
            Dashboard.AllowedRoles.Count == 0 &&
            Dashboard.AllowedClientIds.Count == 0)
        {
            throw new InvalidOperationException(
                "BackgroundTasks:Hangfire:Dashboard requires AllowedRoles or AllowedClientIds when enabled.");
        }
    }
}

public sealed class BackgroundTasksHangfireDashboardOptions
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "/internal/hangfire";
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> AllowedClientIds { get; set; } = [];
}

public static class BackgroundTasksHangfireOptionsPostConfigure
{
    public static void Normalize(BackgroundTasksHangfireOptions options, bool isDevelopment)
    {
        options.ServiceName = NormalizeToken(options.ServiceName ?? "tenantwright-service");
        options.ConnectionStringName = string.IsNullOrWhiteSpace(options.ConnectionStringName)
            ? "Default"
            : options.ConnectionStringName.Trim();
        options.SchemaName = string.IsNullOrWhiteSpace(options.SchemaName)
            ? $"hangfire_{options.ServiceName}"
            : NormalizeToken(options.SchemaName);
        options.PrepareSchemaIfNecessary ??= isDevelopment;
        options.WorkerCount = options.WorkerCount <= 0
            ? Math.Clamp(Environment.ProcessorCount, 1, 20)
            : Math.Clamp(options.WorkerCount, 1, 100);

        if (options.Queues.Count == 0)
        {
            options.Queues =
            [
                $"{options.ServiceName}-critical",
                $"{options.ServiceName}-default",
                $"{options.ServiceName}-low",
                $"{options.ServiceName}-recurring"
            ];
        }

        options.Queues = options.Queues
            .Select(NormalizeQueue)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        options.Dashboard.Path = NormalizeDashboardPath(options.Dashboard.Path);
        options.Dashboard.AllowedRoles = options.Dashboard.AllowedRoles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        options.Dashboard.AllowedClientIds = options.Dashboard.AllowedClientIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string NormalizeQueue(string value)
    {
        var normalized = NormalizeToken(value);
        return normalized.Length <= 50
            ? normalized
            : throw new InvalidOperationException($"Hangfire queue name '{normalized}' exceeds 50 characters.");
    }

    private static string NormalizeToken(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new InvalidOperationException("Background task token cannot be empty.")
            : normalized;
    }

    private static string NormalizeDashboardPath(string value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/internal/hangfire" : value.Trim();
        return path.StartsWith('/') ? path : "/" + path;
    }
}
