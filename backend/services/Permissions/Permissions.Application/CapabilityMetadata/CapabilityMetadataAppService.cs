using Permissions.Application.Contracts.CapabilityMetadata;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Definitions;
using Permissions.Application.FeatureManagement;
using Shared;
using Shared.Modularity.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

namespace Permissions.Application.CapabilityMetadata;

/// <summary>
/// Projects the registered static definition trees into a stable browser-facing capability label
/// source. Dynamic catalog entries and target-specific effective access remain outside this read.
/// </summary>
public sealed class CapabilityMetadataAppService : ICapabilityMetadataAppService, ITransientDependency
{
    private readonly PermissionDefinitionManager _permissionDefinitions;
    private readonly FeatureDefinitionManager _featureDefinitions;

    public CapabilityMetadataAppService(
        PermissionDefinitionManager permissionDefinitions,
        FeatureDefinitionManager featureDefinitions)
    {
        _permissionDefinitions = permissionDefinitions;
        _featureDefinitions = featureDefinitions;
    }

    public Task<CapabilityMetadataResultDto> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var permissions = _permissionDefinitions
            .GetGroups()
            .SelectMany(group => FlattenPermissions(group.Permissions).Select(definition =>
                new PermissionCapabilityMetadataDto
                {
                    Name = definition.Name,
                    DisplayName = definition.DisplayName,
                    Description = null,
                    GroupName = group.Name,
                    GroupDisplayName = group.DisplayName,
                    ParentName = definition.ParentName,
                    ElementType = definition.ElementType?.ToString(),
                    ElementKey = definition.ElementKey,
                    IsEnabled = definition.IsEnabled,
                    AllowsWildcard = definition.AllowsWildcard,
                    RequiresApproval = definition.RequiresApproval,
                    Providers = [.. definition.Providers],
                    Actions = [.. definition.Actions],
                    RequiredFeatures = [.. definition.RequiredFeatures],
                    Source = CapabilityMetadataSource.StaticDefinition
                }))
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToList();

        var features = _featureDefinitions
            .GetGroups()
            .SelectMany(group => FlattenFeatures(group.Features).Select(definition =>
                new FeatureCapabilityMetadataDto
                {
                    Name = definition.Name,
                    DisplayName = definition.DisplayName,
                    Description = null,
                    GroupName = group.Name,
                    GroupDisplayName = group.DisplayName,
                    ParentName = definition.ParentName,
                    ValueType = definition.ValueType,
                    DefaultValue = definition.DefaultValue,
                    IsVisibleToClients = definition.IsVisibleToClients,
                    IsAvailableToHost = definition.IsAvailableToHost,
                    AllowedProviders = [.. definition.AllowedProviders],
                    Source = CapabilityMetadataSource.StaticDefinition
                }))
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(new CapabilityMetadataResultDto
        {
            Permissions = permissions,
            Features = features
        });
    }

    public Task<ValidateFeatureCapabilitiesResultDto> ValidateFeaturesAsync(
        ValidateFeatureCapabilitiesRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Features.Count is < 1 or > 100)
            throw new SharedValidationException("Feature capability validation requires between 1 and 100 entries.");

        var duplicates = request.Features
            .Select(item => item.Name?.Trim() ?? string.Empty)
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Key.Length == 0 || group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
            throw new SharedValidationException("Feature capability names must be non-empty and unique.");

        var definitions = _featureDefinitions.GetGroups()
            .SelectMany(group => FlattenFeatures(group.Features))
            .ToDictionary(definition => definition.Name, StringComparer.Ordinal);
        var result = new List<ValidatedFeatureCapabilityDto>(request.Features.Count);
        foreach (var item in request.Features)
        {
            var name = item.Name.Trim();
            if (!definitions.TryGetValue(name, out var definition) || !definition.IsVisibleToClients)
                throw new SharedValidationException($"Feature capability is unknown or not provisionable: {name}");

            var normalized = FeatureManagementAppService.NormalizeFeatureValue(
                definition.Name,
                definition.ValueType,
                item.Value,
                definition.AllowedValues,
                definition.MinValue,
                definition.MaxValue);
            result.Add(new ValidatedFeatureCapabilityDto
            {
                Name = definition.Name,
                NormalizedValue = normalized,
                ValueType = definition.ValueType,
                DefinitionChecksum = ComputeChecksum(definition)
            });
        }

        return Task.FromResult(new ValidateFeatureCapabilitiesResultDto { Features = result });
    }

    private static string ComputeChecksum(FeatureDefinition definition)
    {
        var canonical = string.Join('\n',
            definition.Name,
            definition.ValueType,
            definition.DefaultValue ?? string.Empty,
            definition.IsVisibleToClients,
            definition.IsAvailableToHost,
            string.Join(',', definition.AllowedProviders.OrderBy(value => value, StringComparer.Ordinal)),
            string.Join(',', definition.AllowedValues.OrderBy(value => value, StringComparer.Ordinal)),
            definition.MinValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            definition.MaxValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static IEnumerable<PermissionDefinition> FlattenPermissions(
        IEnumerable<PermissionDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            yield return definition;

            foreach (var child in FlattenPermissions(definition.Children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<FeatureDefinition> FlattenFeatures(
        IEnumerable<FeatureDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            yield return definition;

            foreach (var child in FlattenFeatures(definition.Children))
            {
                yield return child;
            }
        }
    }
}
