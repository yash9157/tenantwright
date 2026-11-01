using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Definitions;
using Permissions.Domain.FeatureManagement;
using Permissions.Domain.PermissionManagement;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Definitions;

public class PermissionDefinitionSyncService : ITransientDependency
{
    private readonly PermissionDefinitionManager _permissionDefinitions;
    private readonly FeatureDefinitionManager _featureDefinitions;
    private readonly IRepository<PermissionGroupDefinitionRecord, Guid> _permissionGroupRepo;
    private readonly IRepository<PermissionDefinitionRecord, Guid> _permissionRepo;
    private readonly IRepository<FeatureGroupDefinitionRecord, Guid> _featureGroupRepo;
    private readonly IRepository<FeatureDefinitionRecord, Guid> _featureRepo;
    private readonly IRepository<PermissionDelegationDefinition, Guid> _delegationDefinitionRepo;
    private readonly IRepository<PermissionDelegationDefinitionVersion, Guid> _delegationVersionRepo;

    public PermissionDefinitionSyncService(
        PermissionDefinitionManager permissionDefinitions,
        FeatureDefinitionManager featureDefinitions,
        IRepository<PermissionGroupDefinitionRecord, Guid> permissionGroupRepo,
        IRepository<PermissionDefinitionRecord, Guid> permissionRepo,
        IRepository<FeatureGroupDefinitionRecord, Guid> featureGroupRepo,
        IRepository<FeatureDefinitionRecord, Guid> featureRepo,
        IRepository<PermissionDelegationDefinition, Guid> delegationDefinitionRepo,
        IRepository<PermissionDelegationDefinitionVersion, Guid> delegationVersionRepo)
    {
        _permissionDefinitions = permissionDefinitions;
        _featureDefinitions = featureDefinitions;
        _permissionGroupRepo = permissionGroupRepo;
        _permissionRepo = permissionRepo;
        _featureGroupRepo = featureGroupRepo;
        _featureRepo = featureRepo;
        _delegationDefinitionRepo = delegationDefinitionRepo;
        _delegationVersionRepo = delegationVersionRepo;
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await SyncPermissionDefinitionsAsync(cancellationToken);
        await SyncDelegationDefinitionsAsync(cancellationToken);
        await SyncFeatureDefinitionsAsync(cancellationToken);
    }

    private async Task SyncDelegationDefinitionsAsync(CancellationToken cancellationToken)
    {
        var sourceDefinitions = _permissionDefinitions.GetGroups()
            .SelectMany(group => EnumeratePermissions(group.Permissions))
            .Where(permission => permission.Delegation is { Delegable: true } && permission.IsEnabled)
            .OrderBy(permission => permission.Name, StringComparer.Ordinal)
            .ToArray();
        var sourceNames = sourceDefinitions.Select(permission => permission.Name.Trim()).ToHashSet(StringComparer.Ordinal);

        var existingDefinitions = await (await _delegationDefinitionRepo.GetQueryableAsync())
            .ToListAsync(cancellationToken);
        var existingVersions = await (await _delegationVersionRepo.GetQueryableAsync()).ToListAsync(cancellationToken);
        var definitionsByPermission = existingDefinitions.ToDictionary(definition => definition.PermissionName, StringComparer.Ordinal);

        foreach (var source in sourceDefinitions)
        {
            var metadata = source.Delegation!;
            var permissionName = source.Name.Trim();
            if (!definitionsByPermission.TryGetValue(permissionName, out var definition))
            {
                definition = new PermissionDelegationDefinition(Guid.CreateVersion7(), permissionName, metadata.OwningService);
                // The current-version association is a strict two-way database relationship. Persist the
                // descriptor with a null current version before publishing its first immutable version so
                // EF does not attempt a circular insert that PostgreSQL/SQLite correctly reject.
                await _delegationDefinitionRepo.InsertAsync(definition, autoSave: true, cancellationToken: cancellationToken);
                definitionsByPermission.Add(permissionName, definition);
            }

            if (!string.Equals(definition.OwningService, metadata.OwningService, StringComparison.Ordinal))
                throw new InvalidOperationException($"Delegation definition '{permissionName}' changed its owning service.");
            if (definition.Lifecycle == PermissionDelegationDefinitionLifecycle.Retired)
                throw new InvalidOperationException($"Retired delegation definition '{permissionName}' cannot be reactivated.");

            var checksum = ComputeDelegationChecksum(source, metadata);
            var current = definition.CurrentVersionId is { } currentId
                ? existingVersions.SingleOrDefault(version => version.Id == currentId)
                : null;
            if (current is { PublicationState: PermissionDelegationDefinitionPublicationState.Published } &&
                string.Equals(current.SourceDefinitionChecksum, checksum, StringComparison.Ordinal))
                continue;

            if (current is { PublicationState: PermissionDelegationDefinitionPublicationState.Published })
            {
                current.Retire();
                await _delegationVersionRepo.UpdateAsync(current, autoSave: false, cancellationToken: cancellationToken);
            }

            var nextVersion = existingVersions.Where(version => version.DefinitionId == definition.Id)
                .Select(version => version.Version).DefaultIfEmpty(0).Max() + 1;
            var published = new PermissionDelegationDefinitionVersion(
                Guid.CreateVersion7(),
                definition.Id,
                nextVersion,
                checksum,
                metadata.ManagerPermissionName,
                (Permissions.Domain.Shared.Enums.PermissionDelegationTargetKinds)(short)metadata.AllowedTargetKinds,
                (Permissions.Domain.Shared.Enums.PermissionDelegationScopeKinds)(short)metadata.AllowedScopeKinds,
                (Permissions.Domain.Shared.Enums.PermissionDelegationEffects)(short)metadata.AllowedEffects,
                metadata.MaximumExpiry is { } maximumExpiry ? checked((int)maximumExpiry.TotalSeconds) : null,
                metadata.RequiresApproval,
                metadata.ApproverPermissionName,
                metadata.ReviewCadence is { } reviewCadence ? checked((short)reviewCadence.TotalDays) : null,
                metadata.RequiredFeatureName,
                metadata.Delegable,
                DateTime.UtcNow,
                effectiveToUtc: null);
            published.Publish();
            definition.Publish(published.Id);
            await _delegationVersionRepo.InsertAsync(published, autoSave: false, cancellationToken: cancellationToken);
            await _delegationDefinitionRepo.UpdateAsync(definition, autoSave: false, cancellationToken: cancellationToken);
            existingVersions.Add(published);
        }

        foreach (var stale in existingDefinitions.Where(definition =>
                     definition.Lifecycle == PermissionDelegationDefinitionLifecycle.Active &&
                     !sourceNames.Contains(definition.PermissionName)))
        {
            var current = stale.CurrentVersionId is { } currentId
                ? existingVersions.SingleOrDefault(version => version.Id == currentId)
                : null;
            if (current is { PublicationState: PermissionDelegationDefinitionPublicationState.Published })
            {
                current.Retire();
                await _delegationVersionRepo.UpdateAsync(current, autoSave: false, cancellationToken: cancellationToken);
            }
            stale.Retire();
            await _delegationDefinitionRepo.UpdateAsync(stale, autoSave: false, cancellationToken: cancellationToken);
        }
    }

    private static string ComputeDelegationChecksum(PermissionDefinition permission, PermissionDelegationMetadata metadata)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            PermissionName = permission.Name.Trim(),
            permission.IsEnabled,
            metadata.OwningService,
            metadata.ManagerPermissionName,
            metadata.ApproverPermissionName,
            AllowedTargetKinds = (short)metadata.AllowedTargetKinds,
            AllowedScopeKinds = (short)metadata.AllowedScopeKinds,
            AllowedEffects = (short)metadata.AllowedEffects,
            MaximumExpirySeconds = metadata.MaximumExpiry?.TotalSeconds,
            metadata.RequiresApproval,
            ReviewCadenceDays = metadata.ReviewCadence?.TotalDays,
            metadata.RequiredFeatureName,
            metadata.Delegable
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task SyncPermissionDefinitionsAsync(CancellationToken cancellationToken)
    {
        var groups = _permissionDefinitions.GetGroups();

        var groupQueryable = await _permissionGroupRepo.GetQueryableAsync();
        var permissionQueryable = await _permissionRepo.GetQueryableAsync();

        var existingGroups = await groupQueryable.ToListAsync(cancellationToken);
        var existingPermissions = await permissionQueryable
            .Where(x => x.ResourceName == null)
            .ToListAsync(cancellationToken);

        var groupMap = existingGroups.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var permissionMap = existingPermissions.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var groupName = group.Name.Trim();
            var groupDisplayName = (group.DisplayName ?? group.Name).Trim();

            if (!groupMap.TryGetValue(groupName, out var existingGroup))
            {
                existingGroup = new PermissionGroupDefinitionRecord(Guid.CreateVersion7(), groupName, groupDisplayName);
                await _permissionGroupRepo.InsertAsync(existingGroup, autoSave: false, cancellationToken: cancellationToken);
                groupMap[groupName] = existingGroup;
            }
            else if (!string.Equals(existingGroup.DisplayName, groupDisplayName, StringComparison.Ordinal))
            {
                existingGroup.PatchFrom(new PermissionGroupDefinitionRecord(existingGroup.Id, groupName, groupDisplayName));
                await _permissionGroupRepo.UpdateAsync(existingGroup, autoSave: false, cancellationToken: cancellationToken);
            }

            foreach (var permission in EnumeratePermissions(group.Permissions))
            {
                var name = permission.Name.Trim();
                var displayName = (permission.DisplayName ?? permission.Name).Trim();
                var parentName = NormalizeOrNull(permission.ParentName);
                var providers = JoinOrNull(permission.Providers.Select(PermissionProviderNameNormalizer.Normalize));
                var stateCheckers = permission.RequiredFeatures.Count == 0
                    ? null
                    : JsonSerializer.Serialize(permission.RequiredFeatures.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());

                if (!permissionMap.TryGetValue(name, out var existing))
                {
                    var record = new PermissionDefinitionRecord(
                        Guid.CreateVersion7(),
                        groupName,
                        name,
                        resourceName: null,
                        managementPermissionName: null,
                        parentName: parentName,
                        displayName: displayName,
                        isEnabled: permission.IsEnabled,
                        multiTenancySide: 3,
                        providers: providers,
                        stateCheckers: stateCheckers);

                    await _permissionRepo.InsertAsync(record, autoSave: false, cancellationToken: cancellationToken);
                    permissionMap[name] = record;
                    continue;
                }

                var hasChanges =
                    !string.Equals(existing.GroupName, groupName, StringComparison.Ordinal) ||
                    !string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal) ||
                    !string.Equals(existing.ParentName, parentName, StringComparison.Ordinal) ||
                    existing.IsEnabled != permission.IsEnabled ||
                    existing.MultiTenancySide != 3 ||
                    !string.Equals(existing.Providers, providers, StringComparison.Ordinal) ||
                    !string.Equals(existing.StateCheckers, stateCheckers, StringComparison.Ordinal) ||
                    !string.IsNullOrWhiteSpace(existing.ResourceName) ||
                    !string.IsNullOrWhiteSpace(existing.ManagementPermissionName);

                if (!hasChanges)
                {
                    continue;
                }

                var patch = new PermissionDefinitionRecord(
                    existing.Id,
                    groupName,
                    name,
                    resourceName: null,
                    managementPermissionName: null,
                    parentName: parentName,
                    displayName: displayName,
                    isEnabled: permission.IsEnabled,
                    multiTenancySide: 3,
                    providers: providers,
                    stateCheckers: stateCheckers);

                existing.PatchFrom(patch);
                await _permissionRepo.UpdateAsync(existing, autoSave: false, cancellationToken: cancellationToken);
            }
        }
    }

    private async Task SyncFeatureDefinitionsAsync(CancellationToken cancellationToken)
    {
        var groups = _featureDefinitions.GetGroups();

        var groupQueryable = await _featureGroupRepo.GetQueryableAsync();
        var featureQueryable = await _featureRepo.GetQueryableAsync();

        var existingGroups = await groupQueryable.ToListAsync(cancellationToken);
        var existingFeatures = await featureQueryable.ToListAsync(cancellationToken);

        var groupMap = existingGroups.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var featureMap = existingFeatures.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var groupName = group.Name.Trim();
            var groupDisplayName = (group.DisplayName ?? group.Name).Trim();

            if (!groupMap.TryGetValue(groupName, out var existingGroup))
            {
                existingGroup = new FeatureGroupDefinitionRecord(Guid.CreateVersion7(), groupName, groupDisplayName);
                await _featureGroupRepo.InsertAsync(existingGroup, autoSave: false, cancellationToken: cancellationToken);
                groupMap[groupName] = existingGroup;
            }
            else if (!string.Equals(existingGroup.DisplayName, groupDisplayName, StringComparison.Ordinal))
            {
                existingGroup.PatchFrom(new FeatureGroupDefinitionRecord(existingGroup.Id, groupName, groupDisplayName));
                await _featureGroupRepo.UpdateAsync(existingGroup, autoSave: false, cancellationToken: cancellationToken);
            }

            foreach (var feature in EnumerateFeatures(group.Features))
            {
                var name = feature.Name.Trim();
                var displayName = (feature.DisplayName ?? feature.Name).Trim();
                var parentName = NormalizeOrNull(feature.ParentName);
                var allowedProviders = JoinOrNull(feature.AllowedProviders);
                var defaultValue = NormalizeOrNull(feature.DefaultValue);
                var valueType = feature.ValueType.Trim();
                var description = NormalizeOrNull(feature.Description);
                var allowedValues = JoinOrNull(feature.AllowedValues);
                var minValue = feature.MinValue;
                var maxValue = feature.MaxValue;
                var unit = NormalizeOrNull(feature.Unit);

                if (!featureMap.TryGetValue(name, out var existing))
                {
                    var record = new FeatureDefinitionRecord(
                        Guid.CreateVersion7(),
                        groupName,
                        name,
                        parentName,
                        displayName,
                        description: description,
                        defaultValue: defaultValue,
                        isVisibleToClients: feature.IsVisibleToClients,
                        isAvailableToHost: feature.IsAvailableToHost,
                        allowedProviders: allowedProviders,
                        valueType: valueType,
                        allowedValues: allowedValues,
                        minValue: minValue,
                        maxValue: maxValue,
                        unit: unit,
                        mutationPolicy: feature.MutationPolicy,
                        usesAggregateTenantPool: feature.UsesAggregateTenantPool);

                    await _featureRepo.InsertAsync(record, autoSave: false, cancellationToken: cancellationToken);
                    featureMap[name] = record;
                    continue;
                }

                var hasChanges =
                    !string.Equals(existing.GroupName, groupName, StringComparison.Ordinal) ||
                    !string.Equals(existing.ParentName, parentName, StringComparison.Ordinal) ||
                    !string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal) ||
                    !string.Equals(existing.DefaultValue, defaultValue, StringComparison.Ordinal) ||
                    existing.IsVisibleToClients != feature.IsVisibleToClients ||
                    existing.IsAvailableToHost != feature.IsAvailableToHost ||
                    !string.Equals(existing.AllowedProviders, allowedProviders, StringComparison.Ordinal) ||
                    !string.Equals(existing.ValueType, valueType, StringComparison.Ordinal) ||
                    !string.Equals(existing.Description, description, StringComparison.Ordinal) ||
                    !string.Equals(existing.AllowedValues, allowedValues, StringComparison.Ordinal) ||
                    existing.MinValue != minValue ||
                    existing.MaxValue != maxValue ||
                    !string.Equals(existing.Unit, unit, StringComparison.Ordinal) ||
                    existing.MutationPolicy != feature.MutationPolicy ||
                    existing.UsesAggregateTenantPool != feature.UsesAggregateTenantPool;

                if (!hasChanges)
                {
                    continue;
                }

                var patch = new FeatureDefinitionRecord(
                    existing.Id,
                    groupName,
                    name,
                    parentName,
                    displayName,
                    description: description,
                    defaultValue: defaultValue,
                    isVisibleToClients: feature.IsVisibleToClients,
                    isAvailableToHost: feature.IsAvailableToHost,
                    allowedProviders: allowedProviders,
                    valueType: valueType,
                    allowedValues: allowedValues,
                    minValue: minValue,
                    maxValue: maxValue,
                    unit: unit,
                    mutationPolicy: feature.MutationPolicy,
                    usesAggregateTenantPool: feature.UsesAggregateTenantPool);

                existing.PatchFrom(patch);
                await _featureRepo.UpdateAsync(existing, autoSave: false, cancellationToken: cancellationToken);
            }
        }

        // Feature definitions are code-owned; remove any persisted feature whose provider definition
        // was deleted so the store mirrors code. Orphaned tenant values for a removed feature are
        // never returned (GetAsync iterates definitions), so no separate value cleanup is required.
        var definedFeatureNames = groups
            .SelectMany(group => EnumerateFeatures(group.Features))
            .Select(feature => feature.Name.Trim())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in existingFeatures.Where(feature => !definedFeatureNames.Contains(feature.Name)))
        {
            await _featureRepo.DeleteAsync(stale, autoSave: false, cancellationToken: cancellationToken);
        }
    }

    private static IEnumerable<PermissionDefinition> EnumeratePermissions(IEnumerable<PermissionDefinition> permissions)
    {
        foreach (var p in permissions)
        {
            yield return p;
            foreach (var child in EnumeratePermissions(p.Children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<FeatureDefinition> EnumerateFeatures(IEnumerable<FeatureDefinition> features)
    {
        foreach (var f in features)
        {
            yield return f;
            foreach (var child in EnumerateFeatures(f.Children))
            {
                yield return child;
            }
        }
    }

    private static string? NormalizeOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? JoinOrNull(IEnumerable<string> values)
    {
        var distinct = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        return distinct.Length == 0 ? null : string.Join(",", distinct);
    }
}
