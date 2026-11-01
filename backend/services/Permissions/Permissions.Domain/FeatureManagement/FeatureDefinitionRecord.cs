using Permissions.Application.Contracts.Definitions;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.AuditLogs.Auditing;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Domain.FeatureManagement;

[Audited]
public class FeatureDefinitionRecord : BasicAggregateRoot<Guid>, IHasExtraProperties
{
    public string GroupName { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string? ParentName { get; private set; }
    public string DisplayName { get; private set; } = default!;
    public string? Description { get; private set; }
    public string? DefaultValue { get; private set; }
    public bool IsVisibleToClients { get; private set; }
    public bool IsAvailableToHost { get; private set; }
    public string? AllowedProviders { get; private set; }
    public string ValueType { get; private set; } = default!;
    public EntitlementMutationPolicy MutationPolicy { get; private set; }
    public bool UsesAggregateTenantPool { get; private set; }

    /// <summary>Comma-separated permitted values for a <c>Selection</c> feature; null for other types.</summary>
    public string? AllowedValues { get; private set; }

    /// <summary>Inclusive lower/upper bounds for a <c>Number</c> feature; null when unconstrained.</summary>
    public decimal? MinValue { get; private set; }
    public decimal? MaxValue { get; private set; }

    /// <summary>Optional unit label shown next to a <c>Number</c> feature (e.g. "messages").</summary>
    public string? Unit { get; private set; }

    public ExtraPropertyDictionary ExtraProperties { get; private set; } = new();

    protected FeatureDefinitionRecord()
    {
    }

    public FeatureDefinitionRecord(
        Guid id,
        string groupName,
        string name,
        string? parentName,
        string displayName,
        string? description,
        string? defaultValue,
        bool isVisibleToClients,
        bool isAvailableToHost,
        string? allowedProviders,
        string valueType,
        string? allowedValues = null,
        decimal? minValue = null,
        decimal? maxValue = null,
        string? unit = null,
        EntitlementMutationPolicy mutationPolicy = EntitlementMutationPolicy.ProvisioningOnly,
        bool usesAggregateTenantPool = false)
        : base(id)
    {
        GroupName = groupName.Trim();
        Name = name.Trim();
        ParentName = string.IsNullOrWhiteSpace(parentName) ? null : parentName.Trim();
        DisplayName = displayName.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DefaultValue = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim();
        IsVisibleToClients = isVisibleToClients;
        IsAvailableToHost = isAvailableToHost;
        AllowedProviders = string.IsNullOrWhiteSpace(allowedProviders) ? null : allowedProviders.Trim();
        ValueType = valueType.Trim();
        AllowedValues = string.IsNullOrWhiteSpace(allowedValues) ? null : allowedValues.Trim();
        MinValue = minValue;
        MaxValue = maxValue;
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        MutationPolicy = mutationPolicy;
        UsesAggregateTenantPool = usesAggregateTenantPool;
        ExtraProperties = new ExtraPropertyDictionary();
    }

    public void PatchFrom(FeatureDefinitionRecord other)
    {
        GroupName = other.GroupName;
        Name = other.Name;
        ParentName = other.ParentName;
        DisplayName = other.DisplayName;
        Description = other.Description;
        DefaultValue = other.DefaultValue;
        IsVisibleToClients = other.IsVisibleToClients;
        IsAvailableToHost = other.IsAvailableToHost;
        AllowedProviders = other.AllowedProviders;
        ValueType = other.ValueType;
        AllowedValues = other.AllowedValues;
        MinValue = other.MinValue;
        MaxValue = other.MaxValue;
        Unit = other.Unit;
        MutationPolicy = other.MutationPolicy;
        UsesAggregateTenantPool = other.UsesAggregateTenantPool;
        ExtraProperties = new ExtraPropertyDictionary(other.ExtraProperties);
    }
}
