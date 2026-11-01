using System.Reflection;
using Shared.AuditLogs.Auditing;

namespace Shared.AuditLogs.Publishing;

/// <summary>The source service stamped on an audit write request that does not name one itself.</summary>
internal static class AuditSourceService
{
    public static string Resolve(SharedAuditingOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            return options.ApplicationName.Trim();
        }

        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrWhiteSpace(entryAssemblyName))
        {
            return entryAssemblyName.Trim();
        }

        return "unknown-service";
    }
}
