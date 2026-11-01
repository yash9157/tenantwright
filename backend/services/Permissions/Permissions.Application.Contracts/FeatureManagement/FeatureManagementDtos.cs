using Permissions.Application.Contracts.Definitions;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.FeatureManagement;

public class FeatureValueDto
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public class UpdateFeaturesDto
{
    public List<FeatureValueDto> Features { get; set; } = new();

    /// <summary>Optional optimistic-concurrency stamp from the loaded list. When supplied and stale,
    /// the update is rejected with HTTP 409 so the editor reloads rather than overwriting.</summary>
    public long? ExpectedProviderStamp { get; set; }
}

/// <summary>Typed optimistic-concurrency conflict for a feature update (surfaced as HTTP 409).</summary>
public sealed class FeatureConcurrencyConflictDto
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public long CurrentStamp { get; init; }
}

public class FeatureDto
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Value { get; set; } = "";
    public string DefaultValue { get; set; } = "";

    /// <summary>Definition value type (e.g. <c>Toggle</c>) so the editor renders the correct control
    /// and the server validates the submitted value.</summary>
    public string ValueType { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Names of features that must be enabled before this one can be. Lets a client render
    /// prerequisite/dependency rules (e.g. specialty gating) from this single feature response
    /// without a second definition-specific call.</summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary><c>true</c> when this tenant has an explicitly stored value for the feature, i.e. the
    /// value is a deliberate override rather than the inherited default. Lets the editor show an
    /// "Overridden" badge and offer a reset-to-default action.</summary>
    public bool IsOverridden { get; set; }

    /// <summary>Permitted values for a <c>Selection</c> feature so the editor renders a dropdown;
    /// empty for other value types.</summary>
    public List<string> AllowedValues { get; set; } = new();

    /// <summary>Inclusive bounds for a <c>Number</c> feature so the editor can constrain and validate
    /// input; null when unconstrained.</summary>
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }

    /// <summary>Optional unit label shown next to a <c>Number</c> feature (e.g. "messages").</summary>
    public string? Unit { get; set; }
    public EntitlementMutationPolicy MutationPolicy { get; set; }
    public bool UsesAggregateTenantPool { get; set; }
}

public sealed class EntitlementValueDto
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

public sealed class ProvisionTenantEntitlementsDto
{
    public required Guid TenantId { get; init; }
    public required string SourceService { get; init; }
    public required Guid SourceAggregateId { get; init; }
    public required long SourceRevision { get; init; }
    public required DateTimeOffset EffectiveAtUtc { get; init; }
    public List<EntitlementValueDto> Features { get; init; } = new();
}

public enum TenantEntitlementProvisioningOutcome
{
    Applied = 1,
    Duplicate = 2,
    Stale = 3
}

public sealed class TenantEntitlementProvisioningResultDto
{
    public required TenantEntitlementProvisioningOutcome Outcome { get; init; }
    public required long SourceRevision { get; init; }
    public required string ValuesChecksum { get; init; }
    public required long ProviderStamp { get; init; }
    public required DateTimeOffset AcknowledgedAtUtc { get; init; }
}

public class FeatureGroupDto
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<FeatureDto> Features { get; set; } = new();
}

public class GetFeatureListResultDto
{
    public string ProviderName { get; set; } = "";
    public string ProviderKey { get; set; } = "";

    /// <summary>Optimistic-concurrency stamp to echo back on update.</summary>
    public long ProviderStamp { get; set; }

    public List<FeatureGroupDto> Groups { get; set; } = new();
}

public class BatchFeatureCheckRequestDto
{
    public Guid? TenantId { get; set; }
    public List<string> FeatureNames { get; set; } = new();
}

public class BatchFeatureCheckResponseDto
{
    public Dictionary<string, bool> Results { get; set; } = new(StringComparer.Ordinal);
}
