using System;
using Permissions.Application.FeatureManagement;
using Permissions.Domain.Shared.Constants;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// BE-6 typed feature validation: submitted values are validated and canonicalized against their
/// definition value type before being written, so an invalid value never reaches persistence.
/// </summary>
public class FeatureValueValidationTests
{
    [Theory]
    [InlineData("true", "true")]
    [InlineData("TRUE", "true")]
    [InlineData("1", "true")]
    [InlineData("false", "false")]
    [InlineData("0", "false")]
    [InlineData("", "false")]
    public void Toggle_values_are_canonicalized(string input, string expected)
    {
        Assert.Equal(expected, FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Toggle, input));
    }

    [Fact]
    public void Toggle_rejects_a_non_boolean_value()
    {
        Assert.Throws<ArgumentException>(
            () => FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Toggle, "maybe"));
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("3.5", "3.5")]
    public void Number_values_are_parsed_invariantly(string input, string expected)
    {
        Assert.Equal(expected, FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Number, input));
    }

    [Fact]
    public void Number_rejects_a_non_numeric_value()
    {
        Assert.Throws<ArgumentException>(
            () => FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Number, "lots"));
    }

    [Fact]
    public void Free_text_passes_through_unchanged()
    {
        Assert.Equal("clinic-a", FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.FreeText, "clinic-a"));
    }

    [Fact]
    public void Number_within_bounds_is_accepted()
    {
        Assert.Equal(
            "50",
            FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Number, "50", allowedValues: null, minValue: 0, maxValue: 100));
    }

    [Fact]
    public void Number_below_minimum_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Number, "-1", allowedValues: null, minValue: 0, maxValue: 100));
    }

    [Fact]
    public void Number_above_maximum_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Number, "101", allowedValues: null, minValue: 0, maxValue: 100));
    }

    [Fact]
    public void Selection_accepts_a_permitted_value()
    {
        string[] allowed = ["Email", "SMS", "Push"];
        Assert.Equal(
            "SMS",
            FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Selection, "SMS", allowed));
    }

    [Fact]
    public void Selection_rejects_a_value_outside_the_allowed_set()
    {
        string[] allowed = ["Email", "SMS", "Push"];
        Assert.Throws<ArgumentException>(
            () => FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Selection, "Carrier-Pigeon", allowed));
    }

    [Fact]
    public void Selection_allows_an_empty_value_to_clear_the_override()
    {
        string[] allowed = ["Email", "SMS", "Push"];
        Assert.Equal(
            string.Empty,
            FeatureManagementAppService.NormalizeFeatureValue("F", FeatureValueTypes.Selection, "", allowed));
    }

    [Fact]
    public void Enabled_dependent_toggle_with_disabled_prerequisite_is_rejected()
    {
        var exception = Assert.Throws<Shared.SharedValidationException>(() =>
            FeatureManagementAppService.ValidateFeatureDependencies(
                CreateDependentDefinitions(),
                new Dictionary<string, string>(),
                new Dictionary<string, string>
                {
                    [DependentFeatureName] = "true"
                }));

        Assert.Equal(PermissionsErrorCodes.FeatureDependencyNotSatisfied, exception.Code);
        Assert.Collection(
            exception.Errors![DependentFeatureName],
            prerequisite => Assert.Equal(BaseFeatureName, prerequisite));
    }

    [Fact]
    public void Prerequisite_and_dependent_toggle_can_be_enabled_in_the_same_batch()
    {
        FeatureManagementAppService.ValidateFeatureDependencies(
            CreateDependentDefinitions(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                [BaseFeatureName] = "true",
                [DependentFeatureName] = "true"
            });
    }

    [Fact]
    public void Disabling_prerequisite_while_persisted_dependent_remains_enabled_is_rejected()
    {
        Assert.Throws<Shared.SharedValidationException>(() =>
            FeatureManagementAppService.ValidateFeatureDependencies(
                CreateDependentDefinitions(),
                EnabledDependentValues(),
                new Dictionary<string, string>
                {
                    [BaseFeatureName] = "false"
                }));
    }

    [Fact]
    public void Prerequisite_and_dependent_toggle_can_be_disabled_in_the_same_batch()
    {
        FeatureManagementAppService.ValidateFeatureDependencies(
            CreateDependentDefinitions(),
            EnabledDependentValues(),
            new Dictionary<string, string>
            {
                [BaseFeatureName] = "false",
                [DependentFeatureName] = "false"
            });
    }

    [Fact]
    public void Numeric_dependent_value_is_not_treated_as_an_enabled_toggle()
    {
        var definitions = CreateDependentDefinitions();
        var limit = new Permissions.Application.Contracts.Definitions.FeatureDefinition(
            "Example.MonthlyLimit",
            "Monthly limit");
        limit.DefaultValue = "0";
        limit.AsNumber(minValue: 0)
            .Requires(BaseFeatureName);
        definitions.Add(limit);

        FeatureManagementAppService.ValidateFeatureDependencies(
            definitions,
            new Dictionary<string, string>
            {
                ["Example.MonthlyLimit"] = "100"
            },
            new Dictionary<string, string>());
    }

    private const string BaseFeatureName = "Example.Base";
    private const string DependentFeatureName = "Example.Dependent";

    private static List<Permissions.Application.Contracts.Definitions.FeatureDefinition> CreateDependentDefinitions()
    {
        var context = new Permissions.Application.Contracts.Definitions.FeatureDefinitionContext();
        var group = context.AddGroup("Example", "Example");

        var prerequisite = group.AddFeature(BaseFeatureName, "Base");
        prerequisite.DefaultValue = "false";

        var dependent = group.AddFeature(DependentFeatureName, "Dependent");
        dependent.DefaultValue = "false";
        dependent.Requires(BaseFeatureName);

        return group.Features;
    }

    private static Dictionary<string, string> EnabledDependentValues()
    {
        return new Dictionary<string, string>
        {
            [BaseFeatureName] = "true",
            [DependentFeatureName] = "true"
        };
    }
}
