namespace Permissions.Application.Contracts.Definitions;

public sealed class PermissionDefinition
{
    public string Name { get; }

    public string? DisplayName { get; set; }

    public string? ParentName { get; }

    public PermissionElementType? ElementType { get; set; }

    public string? ElementKey { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool AllowsWildcard { get; set; } = true;

    /// <summary>
    /// When true, granting this permission (allow) is routed through the approval workflow (flow 6):
    /// a grant becomes a Pending PermissionGrantChangeRequest that a different admin must approve
    /// before it is applied. Revokes/denies still apply directly. Inherited by children unless a
    /// child sets its own value.
    /// </summary>
    public bool RequiresApproval { get; set; }

    /// <summary>
    /// Server-authored, typed delegation constraints for a bounded resource-access flow.
    /// Null preserves ordinary permission definitions and does not create a second registry.
    /// </summary>
    public PermissionDelegationMetadata? Delegation { get; private set; }

    public List<string> Providers { get; } = new();

    public List<string> Actions { get; } = new();

    public List<string> RequiredFeatures { get; } = new();

    public List<PermissionDefinition> Children { get; } = new();

    public PermissionDefinition(string name, string? displayName = null, string? parentName = null)
    {
        Name = name;
        DisplayName = displayName;
        ParentName = parentName;
    }

    public PermissionDefinition AddChild(string name, string? displayName = null)
    {
        var child = new PermissionDefinition(name, displayName, Name);
        Children.Add(child);
        return child;
    }

    public PermissionDefinition RequireApproval()
    {
        RequiresApproval = true;
        return this;
    }

    public PermissionDefinition ConfigureDelegation(PermissionDelegationMetadata metadata)
    {
        Delegation = metadata ?? throw new ArgumentNullException(nameof(metadata));
        return this;
    }

    public PermissionDefinition RequireFeatures(params string[] featureNames)
    {
        foreach (var featureName in featureNames)
        {
            if (!string.IsNullOrWhiteSpace(featureName))
            {
                RequiredFeatures.Add(featureName.Trim());
            }
        }

        return this;
    }
}

[Flags]
public enum PermissionDelegationTargetKinds : short
{
    None = 0,
    User = 1,
    Role = 2
}

[Flags]
public enum PermissionDelegationScopeKinds : short
{
    None = 0,
    Tenant = 1,
    Resource = 4
}

[Flags]
public enum PermissionDelegationEffects : short
{
    None = 0,
    Allow = 1,
    Deny = 2
}

public sealed class PermissionDelegationMetadata
{
    public string OwningService { get; }
    public string ManagerPermissionName { get; }
    public string? ApproverPermissionName { get; }
    public PermissionDelegationTargetKinds AllowedTargetKinds { get; }
    public PermissionDelegationScopeKinds AllowedScopeKinds { get; }
    public PermissionDelegationEffects AllowedEffects { get; }
    public TimeSpan? MaximumExpiry { get; }
    public bool RequiresApproval { get; }
    public TimeSpan? ReviewCadence { get; }
    public string? RequiredFeatureName { get; }
    public bool Delegable { get; }

    public PermissionDelegationMetadata(
        string owningService,
        string managerPermissionName,
        string? approverPermissionName,
        PermissionDelegationTargetKinds allowedTargetKinds,
        PermissionDelegationScopeKinds allowedScopeKinds,
        PermissionDelegationEffects allowedEffects,
        TimeSpan? maximumExpiry,
        bool requiresApproval,
        TimeSpan? reviewCadence,
        string? requiredFeatureName,
        bool delegable)
    {
        OwningService = Require(owningService, nameof(owningService), 64);
        ManagerPermissionName = Require(managerPermissionName, nameof(managerPermissionName), 256);
        ApproverPermissionName = Optional(approverPermissionName, nameof(approverPermissionName), 256);
        ValidateFlags(allowedTargetKinds, PermissionDelegationTargetKinds.User | PermissionDelegationTargetKinds.Role, nameof(allowedTargetKinds));
        ValidateFlags(allowedScopeKinds, PermissionDelegationScopeKinds.Tenant | PermissionDelegationScopeKinds.Resource, nameof(allowedScopeKinds));
        ValidateFlags(allowedEffects, PermissionDelegationEffects.Allow | PermissionDelegationEffects.Deny, nameof(allowedEffects));
        if (maximumExpiry is { } expiry && expiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumExpiry));
        if (reviewCadence is { } cadence && cadence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reviewCadence));
        if (maximumExpiry?.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumExpiry));
        if (maximumExpiry is { } maximumExpiryValue && maximumExpiryValue.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(maximumExpiry));
        if (reviewCadence is { } cadenceValue &&
            (cadenceValue.TotalDays > short.MaxValue || cadenceValue != TimeSpan.FromDays(Math.Truncate(cadenceValue.TotalDays))))
            throw new ArgumentOutOfRangeException(nameof(reviewCadence));
        if (requiresApproval != (ApproverPermissionName != null))
            throw new ArgumentException("An approver permission is required exactly when approval is required.", nameof(approverPermissionName));
        if (delegable && (allowedScopeKinds & PermissionDelegationScopeKinds.Resource) == 0)
            throw new ArgumentException("Delegable permissions must declare a resource scope.", nameof(allowedScopeKinds));

        AllowedTargetKinds = allowedTargetKinds;
        AllowedScopeKinds = allowedScopeKinds;
        AllowedEffects = allowedEffects;
        MaximumExpiry = maximumExpiry;
        RequiresApproval = requiresApproval;
        ReviewCadence = reviewCadence;
        RequiredFeatureName = Optional(requiredFeatureName, nameof(requiredFeatureName), 256);
        Delegable = delegable;
    }

    private static void ValidateFlags<T>(T value, T known, string parameterName) where T : struct, Enum
    {
        var numericValue = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        var numericKnown = Convert.ToInt64(known, System.Globalization.CultureInfo.InvariantCulture);
        if (numericValue == 0 || (numericValue & ~numericKnown) != 0)
            throw new ArgumentException("At least one known delegation value is required.", parameterName);
    }

    private static string Require(string value, string parameterName, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximumLength
            ? throw new ArgumentException("A bounded non-empty value is required.", parameterName)
            : value.Trim();

    private static string? Optional(string? value, string parameterName, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Require(value, parameterName, maximumLength);
}

public sealed class PermissionGroupDefinition
{
    public string Name { get; }

    public string? DisplayName { get; set; }

    public List<PermissionDefinition> Permissions { get; } = new();

    public PermissionGroupDefinition(string name, string? displayName = null)
    {
        Name = name;
        DisplayName = displayName;
    }

    public PermissionDefinition AddPermission(string name, string? displayName = null)
    {
        var permission = new PermissionDefinition(name, displayName);
        Permissions.Add(permission);
        return permission;
    }
}

public interface IPermissionDefinitionContext
{
    PermissionGroupDefinition AddGroup(string name, string? displayName = null);
    PermissionGroupDefinition? GetGroupOrNull(string name);
}

public sealed class PermissionDefinitionContext : IPermissionDefinitionContext
{
    public Dictionary<string, PermissionGroupDefinition> Groups { get; } = new(StringComparer.Ordinal);

    public PermissionGroupDefinition AddGroup(string name, string? displayName = null)
    {
        if (Groups.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var group = new PermissionGroupDefinition(name, displayName);
        Groups.Add(name, group);
        return group;
    }

    public PermissionGroupDefinition? GetGroupOrNull(string name)
    {
        return Groups.TryGetValue(name, out var group) ? group : null;
    }
}

public abstract class PermissionDefinitionProvider
{
    public abstract void Define(IPermissionDefinitionContext context);
}
