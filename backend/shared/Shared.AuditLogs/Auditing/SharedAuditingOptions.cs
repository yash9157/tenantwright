using System.IO;
using System.Linq.Expressions;
using System.Threading;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.Auditing;

public sealed class SharedAuditingOptions
{
    public const string SectionName = "AuditLogging";

    public bool HideErrors { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public string? ApplicationName { get; set; }

    public bool IsEnabledForAnonymousUsers { get; set; } = true;

    public bool AlwaysLogOnException { get; set; } = true;

    public bool IsEnabledForIntegrationServices { get; set; } = true;

    public bool SaveEntityHistoryWhenNavigationChanges { get; set; } = true;

    public bool IsEnabledForGetRequests { get; set; }

    public bool DisableLogActionInfo { get; set; }

    public bool RequireExplicitControllerAuditing { get; set; }

    public bool RequireExplicitServiceAuditing { get; set; }

    public bool WriteRequestAuditOnlyForAuditedEndpoints { get; set; }

    public AuditEnforcementMode EnforcementMode { get; set; } = AuditEnforcementMode.Observe;

    /// <summary>
    /// Audit categories that fail-closed on write failure: if the audit event can't be published (e.g.
    /// the event bus/outbox is briefly unavailable), the exception propagates and the underlying request
    /// fails, instead of being swallowed by <see cref="HideErrors"/>. Opt-in per category rather than a
    /// global flip, because a global fail-closed policy would make every audited operation an
    /// availability risk on the audit pipeline. Reserve this for security-critical categories (e.g.
    /// authentication, permission grants, break-glass access) where an unaudited action is worse than a
    /// failed request. Category comparison is case-insensitive. Empty by default (fully Observe).
    /// </summary>
    public List<string> StrictFailureCategories { get; set; } = [];

    /// <summary>
    /// True if <paramref name="category"/> is configured to fail-closed via
    /// <see cref="StrictFailureCategories"/>.
    /// </summary>
    public bool IsStrictFailureCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || StrictFailureCategories.Count == 0)
        {
            return false;
        }

        var trimmed = category.Trim();
        return StrictFailureCategories.Any(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public List<Func<AuditLogInfo, Task<bool>>> AlwaysLogSelectors { get; } = [];

    public List<AuditLogContributor> Contributors { get; } = [];

    public List<Type> IgnoredTypes { get; } =
    [
        typeof(Stream),
        typeof(Expression),
        typeof(CancellationToken)
    ];

    public List<NamedTypeSelector> EntityHistorySelectors { get; } = [];
}

public enum AuditEnforcementMode
{
    Observe = 0,
    Strict = 1
}

public static class SharedAuditingOptionsPostConfigure
{
    public static void Normalize(SharedAuditingOptions options)
    {
        options ??= new SharedAuditingOptions();

        options.ApplicationName = string.IsNullOrWhiteSpace(options.ApplicationName)
            ? null
            : options.ApplicationName.Trim();

        var normalizedCategories = options.StrictFailureCategories
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        options.StrictFailureCategories.Clear();
        options.StrictFailureCategories.AddRange(normalizedCategories);
    }
}
