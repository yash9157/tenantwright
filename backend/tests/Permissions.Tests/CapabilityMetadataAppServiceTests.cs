using Permissions.Application.CapabilityMetadata;
using Permissions.Application.Contracts.CapabilityMetadata;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Definitions;
using Xunit;

namespace Permissions.Tests;

public sealed class CapabilityMetadataAppServiceTests
{
    [Fact]
    public async Task Static_definitions_are_flattened_into_stable_capability_metadata()
    {
        var permissionManager = new PermissionDefinitionManager([new TestPermissionDefinitions()]);
        var featureManager = new FeatureDefinitionManager([new TestFeatureDefinitions()]);
        var service = new CapabilityMetadataAppService(permissionManager, featureManager);

        var result = await service.GetAsync();

        Assert.Equal(2, result.Permissions.Count);
        var permission = Assert.Single(result.Permissions, item => item.Name == "Operations.Grant");
        Assert.Equal("Operations.Grant", permission.Name);
        Assert.Equal("Grant operations", permission.DisplayName);
        Assert.Equal("Operations", permission.GroupName);
        Assert.True(permission.RequiresApproval);
        Assert.Equal(["Modules.Advanced"], permission.RequiredFeatures);
        Assert.Equal(CapabilityMetadataSource.StaticDefinition, permission.Source);
        Assert.Null(permission.Description);

        var feature = Assert.Single(result.Features, item => item.Name == "Modules.Advanced");
        Assert.Equal("Modules.Advanced", feature.Name);
        Assert.Equal("Advanced module", feature.DisplayName);
        Assert.Equal("Toggle", feature.ValueType);
        Assert.Equal("false", feature.DefaultValue);
        Assert.True(feature.IsAvailableToHost);
        Assert.Equal(CapabilityMetadataSource.StaticDefinition, feature.Source);
        Assert.Null(feature.Description);
    }

    [Fact]
    public async Task Provisionable_feature_values_are_normalized_and_bound_to_a_stable_definition_checksum()
    {
        var service = new CapabilityMetadataAppService(
            new PermissionDefinitionManager([new TestPermissionDefinitions()]),
            new FeatureDefinitionManager([new TestFeatureDefinitions()]));

        var first = await service.ValidateFeaturesAsync(new ValidateFeatureCapabilitiesRequestDto
        {
            Features = [new FeatureCapabilityValueDto { Name = "Modules.Advanced", Value = "TRUE" }]
        });
        var second = await service.ValidateFeaturesAsync(new ValidateFeatureCapabilitiesRequestDto
        {
            Features = [new FeatureCapabilityValueDto { Name = "Modules.Advanced", Value = "true" }]
        });

        var validated = Assert.Single(first.Features);
        Assert.Equal("true", validated.NormalizedValue);
        Assert.Equal("Toggle", validated.ValueType);
        Assert.Equal(64, validated.DefinitionChecksum.Length);
        Assert.Equal(validated.DefinitionChecksum, Assert.Single(second.Features).DefinitionChecksum);
    }

    [Fact]
    public async Task Unknown_duplicate_and_non_provisionable_features_fail_closed()
    {
        var service = new CapabilityMetadataAppService(
            new PermissionDefinitionManager([new TestPermissionDefinitions()]),
            new FeatureDefinitionManager([new TestFeatureDefinitions()]));

        await Assert.ThrowsAsync<Shared.SharedValidationException>(() => service.ValidateFeaturesAsync(new ValidateFeatureCapabilitiesRequestDto
        {
            Features = [new FeatureCapabilityValueDto { Name = "Unknown", Value = "true" }]
        }));
        await Assert.ThrowsAsync<Shared.SharedValidationException>(() => service.ValidateFeaturesAsync(new ValidateFeatureCapabilitiesRequestDto
        {
            Features =
            [
                new FeatureCapabilityValueDto { Name = "Modules.Advanced", Value = "true" },
                new FeatureCapabilityValueDto { Name = "Modules.Advanced", Value = "false" }
            ]
        }));
        await Assert.ThrowsAsync<Shared.SharedValidationException>(() => service.ValidateFeaturesAsync(new ValidateFeatureCapabilitiesRequestDto
        {
            Features = [new FeatureCapabilityValueDto { Name = "Internal.Only", Value = "true" }]
        }));
    }

    private sealed class TestPermissionDefinitions : PermissionDefinitionProvider
    {
        public override void Define(IPermissionDefinitionContext context)
        {
            context
                .AddGroup("Operations", "Operations")
                .AddPermission("Operations", "Operations")
                .AddChild("Operations.Grant", "Grant operations")
                .RequireApproval()
                .RequireFeatures("Modules.Advanced");
        }
    }

    private sealed class TestFeatureDefinitions : FeatureDefinitionProvider
    {
        public override void Define(IFeatureDefinitionContext context)
        {
            var group = context.AddGroup("Specialties", "Specialties");
            group.AddFeature("Modules.Advanced", "Advanced module");
            group.AddFeature("Internal.Only", "Internal only").IsVisibleToClients = false;
        }
    }
}
