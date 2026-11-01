namespace Permissions.Application.Contracts.Definitions;

public sealed class FeatureDefinition
{
    public string Name { get; }
    public string? DisplayName { get; set; }
    public string? ParentName { get; }
    public string ValueType { get; set; } = "Toggle";
    public string DefaultValue { get; set; } = "false";
    public string? Description { get; set; }
    public bool IsVisibleToClients { get; set; } = true;
    public bool IsAvailableToHost { get; set; } = true;
    public List<string> AllowedProviders { get; } = new();
    public EntitlementMutationPolicy MutationPolicy { get; private set; } = EntitlementMutationPolicy.ProvisioningOnly;
    public bool UsesAggregateTenantPool { get; private set; }
    public List<FeatureDefinition> Children { get; } = new();

    /// <summary>Permitted values for a <c>Selection</c> feature. Empty for non-selection types.</summary>
    public List<string> AllowedValues { get; } = new();

    /// <summary>Inclusive bounds and unit label for a <c>Number</c> feature; null when unconstrained.</summary>
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public string? Unit { get; set; }

    /// <summary>
    /// Names of features that must themselves be enabled before this one can be. Unlike
    /// <see cref="ParentName"/> this expresses a prerequisite without moving the feature out of its
    /// group's flat <see cref="FeatureGroupDefinition.Features"/> list, so every existing reader keeps
    /// seeing it — it only gains a reason it may be unselectable.
    /// </summary>
    public List<string> DependsOn { get; } = new();

    public FeatureDefinition(string name, string? displayName = null, string? parentName = null)
    {
        Name = name;
        DisplayName = displayName;
        ParentName = parentName;
    }

    /// <summary>Declares that this feature requires the named features to be enabled first.</summary>
    public FeatureDefinition Requires(params string[] featureNames)
    {
        foreach (var featureName in featureNames)
        {
            if (!string.IsNullOrWhiteSpace(featureName) && !DependsOn.Contains(featureName, StringComparer.Ordinal))
            {
                DependsOn.Add(featureName);
            }
        }

        return this;
    }

    /// <summary>Sets the operator-facing description shown in the entitlements editor.</summary>
    public FeatureDefinition WithDescription(string? description)
    {
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        return this;
    }

    /// <summary>Marks this as a single-choice feature whose value must be one of <paramref name="allowedValues"/>.</summary>
    public FeatureDefinition AsSelection(params string[] allowedValues)
    {
        ValueType = "Selection";
        foreach (var allowedValue in allowedValues)
        {
            if (!string.IsNullOrWhiteSpace(allowedValue) && !AllowedValues.Contains(allowedValue, StringComparer.Ordinal))
            {
                AllowedValues.Add(allowedValue.Trim());
            }
        }

        return this;
    }

    /// <summary>Marks this as a numeric feature with optional inclusive bounds and a unit label.</summary>
    public FeatureDefinition AsNumber(decimal? minValue = null, decimal? maxValue = null, string? unit = null)
    {
        ValueType = "Number";
        MinValue = minValue;
        MaxValue = maxValue;
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        return this;
    }

    public FeatureDefinition AllowTenantAllocation(bool usesAggregateTenantPool = false)
    {
        MutationPolicy = EntitlementMutationPolicy.TenantAllocationAllowed;
        UsesAggregateTenantPool = usesAggregateTenantPool;
        return this;
    }

    public FeatureDefinition AllowProviders(params string[] providerNames)
    {
        foreach (var providerName in providerNames.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
        {
            if (!AllowedProviders.Contains(providerName, StringComparer.Ordinal)) AllowedProviders.Add(providerName);
        }
        return this;
    }

    public FeatureDefinition AddChild(string name, string? displayName = null)
    {
        var child = new FeatureDefinition(name, displayName, Name);
        Children.Add(child);
        return child;
    }
}

public sealed class FeatureGroupDefinition
{
    public string Name { get; }
    public string? DisplayName { get; set; }
    public List<FeatureDefinition> Features { get; } = new();

    public FeatureGroupDefinition(string name, string? displayName = null)
    {
        Name = name;
        DisplayName = displayName;
    }

    public FeatureDefinition AddFeature(string name, string? displayName = null)
    {
        var feature = new FeatureDefinition(name, displayName);
        Features.Add(feature);
        return feature;
    }
}

public interface IFeatureDefinitionContext
{
    FeatureGroupDefinition AddGroup(string name, string? displayName = null);
    FeatureGroupDefinition? GetGroupOrNull(string name);
}

public sealed class FeatureDefinitionContext : IFeatureDefinitionContext
{
    public Dictionary<string, FeatureGroupDefinition> Groups { get; } = new(StringComparer.Ordinal);

    public FeatureGroupDefinition AddGroup(string name, string? displayName = null)
    {
        if (Groups.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var group = new FeatureGroupDefinition(name, displayName);
        Groups.Add(name, group);
        return group;
    }

    public FeatureGroupDefinition? GetGroupOrNull(string name)
    {
        return Groups.TryGetValue(name, out var group) ? group : null;
    }
}

public abstract class FeatureDefinitionProvider
{
    public abstract void Define(IFeatureDefinitionContext context);
}
