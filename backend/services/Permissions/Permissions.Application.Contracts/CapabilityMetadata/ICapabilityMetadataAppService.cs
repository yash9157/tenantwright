namespace Permissions.Application.Contracts.CapabilityMetadata;

/// <summary>
/// Read-only static metadata for registered permission and feature definitions. This projection
/// deliberately excludes dynamic catalog data, target-specific impact, and effective grant state.
/// </summary>
public interface ICapabilityMetadataAppService
{
    Task<CapabilityMetadataResultDto> GetAsync(CancellationToken cancellationToken = default);

    Task<ValidateFeatureCapabilitiesResultDto> ValidateFeaturesAsync(
        ValidateFeatureCapabilitiesRequestDto request,
        CancellationToken cancellationToken = default);
}

public sealed class ValidateFeatureCapabilitiesRequestDto
{
    public List<FeatureCapabilityValueDto> Features { get; set; } = [];
}

public sealed class FeatureCapabilityValueDto
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class ValidateFeatureCapabilitiesResultDto
{
    public List<ValidatedFeatureCapabilityDto> Features { get; set; } = [];
}

public sealed class ValidatedFeatureCapabilityDto
{
    public string Name { get; set; } = string.Empty;

    public string NormalizedValue { get; set; } = string.Empty;

    public string ValueType { get; set; } = string.Empty;

    public string DefinitionChecksum { get; set; } = string.Empty;
}

public sealed class CapabilityMetadataResultDto
{
    public List<PermissionCapabilityMetadataDto> Permissions { get; set; } = [];

    public List<FeatureCapabilityMetadataDto> Features { get; set; } = [];
}

public enum CapabilityMetadataSource
{
    StaticDefinition = 1
}

public sealed class PermissionCapabilityMetadataDto
{
    public string Name { get; set; } = default!;

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string GroupName { get; set; } = default!;

    public string? GroupDisplayName { get; set; }

    public string? ParentName { get; set; }

    public string? ElementType { get; set; }

    public string? ElementKey { get; set; }

    public bool IsEnabled { get; set; }

    public bool AllowsWildcard { get; set; }

    public bool RequiresApproval { get; set; }

    public List<string> Providers { get; set; } = [];

    public List<string> Actions { get; set; } = [];

    public List<string> RequiredFeatures { get; set; } = [];

    public CapabilityMetadataSource Source { get; set; }
}

public sealed class FeatureCapabilityMetadataDto
{
    public string Name { get; set; } = default!;

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string GroupName { get; set; } = default!;

    public string? GroupDisplayName { get; set; }

    public string? ParentName { get; set; }

    public string ValueType { get; set; } = default!;

    public string DefaultValue { get; set; } = default!;

    public bool IsVisibleToClients { get; set; }

    public bool IsAvailableToHost { get; set; }

    public List<string> AllowedProviders { get; set; } = [];

    public CapabilityMetadataSource Source { get; set; }
}
