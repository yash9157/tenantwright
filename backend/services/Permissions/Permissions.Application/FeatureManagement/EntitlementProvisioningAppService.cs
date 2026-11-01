using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Definitions;
using Permissions.Domain.FeatureManagement;
using Permissions.Domain.Shared.Constants;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.AuditLogs.Auditing;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Attributes;

namespace Permissions.Application.FeatureManagement;

[ExposeServices(typeof(IEntitlementProvisioningAppService))]
public sealed class EntitlementProvisioningAppService(
    IRepository<FeatureDefinitionRecord, Guid> definitionRepository,
    IRepository<FeatureValue, Guid> valueRepository,
    FeatureDefinitionManager featureDefinitions,
    FeatureProviderStateManager providerStates,
    IDbContextProvider<PermissionsDbContextApp> dbContextProvider,
    TenantFeatureCacheSyncService cacheSync,
    PermissionStampService stampService,
    TimeProvider timeProvider)
    : IEntitlementProvisioningAppService, ITransientDependency
{
    private const string FeatureStampProviderName = "Feature";

    [Audited]
    [UnitOfWork(true)]
    public async Task<TenantEntitlementProvisioningResultDto> ProvisionTenantAsync(
        ProvisionTenantEntitlementsDto input,
        CancellationToken cancellationToken = default)
    {
        if (input.TenantId == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.SourceService)) throw new ArgumentException("Source service is required.", nameof(input));
        if (input.SourceAggregateId == Guid.Empty) throw new ArgumentException("Source aggregate is required.", nameof(input));
        if (input.SourceRevision <= 0) throw new ArgumentOutOfRangeException(nameof(input));
        if (input.EffectiveAtUtc == default) throw new ArgumentException("Effective time is required.", nameof(input));
        if (input.Features.Count > 500) throw new ArgumentException("At most 500 entitlement values can be provisioned at once.", nameof(input));
        if (input.Features.Any(x => string.IsNullOrWhiteSpace(x.Name)))
            throw new ArgumentException("Entitlement names cannot be blank.", nameof(input));
        if (input.Features.GroupBy(x => x.Name.Trim(), StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Entitlement names must be unique.", nameof(input));

        var sourceService = input.SourceService.Trim();
        var updates = input.Features.ToArray();
        var valuesChecksum = ComputeChecksum(updates);
        var acknowledgedAtUtc = timeProvider.GetUtcNow();
        var dbContext = await dbContextProvider.GetDbContextAsync();
        var latestReceipt = await dbContext.TenantEntitlementProvisioningReceipts
            .Where(x => x.TenantId == input.TenantId && x.SourceService == sourceService)
            .OrderByDescending(x => x.EffectiveAtUtc)
            .ThenByDescending(x => x.SourceRevision)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestReceipt is not null)
        {
            var replayOutcome = ClassifyReplay(
                latestReceipt.SourceAggregateId,
                latestReceipt.SourceRevision,
                latestReceipt.EffectiveAtUtc,
                latestReceipt.ValuesChecksum,
                input.SourceAggregateId,
                input.SourceRevision,
                input.EffectiveAtUtc,
                valuesChecksum);
            if (replayOutcome is not null)
            {
                return new TenantEntitlementProvisioningResultDto
                {
                    Outcome = replayOutcome.Value,
                    SourceRevision = latestReceipt.SourceRevision,
                    ValuesChecksum = latestReceipt.ValuesChecksum,
                    ProviderStamp = latestReceipt.ProviderStamp,
                    AcknowledgedAtUtc = latestReceipt.AppliedAtUtc
                };
            }
        }

        var names = updates.Select(x => x.Name.Trim()).ToArray();
        var tenantKey = input.TenantId.ToString("D");
        var valueQuery = await valueRepository.GetQueryableAsync();
        var tenantValues = await valueQuery
            .Where(x => x.TenantId == input.TenantId &&
                x.ProviderName == FeatureProviderNames.Tenant && x.ProviderKey == tenantKey)
            .ToListAsync(cancellationToken);
        var managedValues = tenantValues.Where(x => x.IsManagedBy(sourceService)).ToArray();
        var affectedNames = names.Concat(managedValues.Select(x => x.Name))
            .Distinct(StringComparer.Ordinal).ToArray();
        var definitionQuery = await definitionRepository.GetQueryableAsync();
        var definitions = await definitionQuery.Where(x => affectedNames.Contains(x.Name)).ToListAsync(cancellationToken);
        var definitionMap = definitions.ToDictionary(x => x.Name, StringComparer.Ordinal);
        if (definitionMap.Count != affectedNames.Length)
            throw new ArgumentException("One or more feature definitions are unknown.", nameof(input));
        foreach (var definition in definitions)
        {
            if (!Split(definition.AllowedProviders).Contains(FeatureProviderNames.Tenant, StringComparer.Ordinal))
                throw new ArgumentException($"Feature '{definition.Name}' cannot be provisioned for tenants.", nameof(input));
        }
        var providerState = await providerStates.FindAsync(
            input.TenantId,
            FeatureStampProviderName,
            tenantKey,
            cancellationToken: cancellationToken);
        var existing = tenantValues.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            var definition = definitionMap[update.Name.Trim()];
            var value = FeatureManagementAppService.NormalizeFeatureValue(definition.Name, definition.ValueType, update.Value.Trim(),
                Split(definition.AllowedValues), definition.MinValue, definition.MaxValue);
            normalized[definition.Name] = value;
        }

        FeatureManagementAppService.ValidateFeatureDependencies(
            featureDefinitions.GetGroups().SelectMany(group => group.Features),
            existing.Where(item => !item.Value.IsManagedBy(sourceService))
                .ToDictionary(item => item.Key, item => item.Value.Value, StringComparer.Ordinal),
            normalized);

        foreach (var removed in managedValues.Where(x => !normalized.ContainsKey(x.Name)))
        {

            await valueRepository.DeleteAsync(removed, autoSave: false, cancellationToken: cancellationToken);
        }

        foreach (var (name, value) in normalized)
        {
            if (existing.TryGetValue(name, out var entity))
            {
                if (!entity.IsManagedBy(sourceService) && entity.ProvisioningSource is not null)
                    throw new InvalidOperationException($"Feature '{name}' is managed by a different provisioning source.");
                if (entity.ProvisioningSource is null)
                    throw new InvalidOperationException($"Feature '{name}' is operator-managed and cannot be overwritten by provisioning.");
                entity.ApplyProvisionedValue(value, sourceService, input.SourceAggregateId,
                    input.SourceRevision, input.EffectiveAtUtc);
                await valueRepository.UpdateAsync(entity, autoSave: false, cancellationToken: cancellationToken);
            }
            else
            {
                var newValue = FeatureValue.CreateProvisioned(
                    Guid.CreateVersion7(), name, value, FeatureProviderNames.Tenant, tenantKey,
                    input.TenantId, sourceService, input.SourceAggregateId, input.SourceRevision,
                    input.EffectiveAtUtc);
                await valueRepository.InsertAsync(newValue, autoSave: false, cancellationToken: cancellationToken);
            }
        }

        providerState ??= await providerStates.CreateAsync(
            input.TenantId,
            FeatureStampProviderName,
            tenantKey,
            cancellationToken);
        providerState.Advance(acknowledgedAtUtc.UtcDateTime);

        dbContext.TenantEntitlementProvisioningReceipts.Add(new TenantEntitlementProvisioningReceipt(
            Guid.CreateVersion7(), input.TenantId, sourceService, input.SourceAggregateId,
            input.SourceRevision, input.EffectiveAtUtc, valuesChecksum, providerState.Stamp,
            acknowledgedAtUtc));

        // Tenant provisioning and the operator feature editor share this durable provider row.
        // Flush it with the values so a concurrent writer cannot commit a different stale snapshot.
        await dbContext.SaveChangesAsync(cancellationToken);

        await cacheSync.SyncAsync(input.TenantId, affectedNames, cancellationToken);
        await stampService.BumpAsync(
            input.TenantId,
            FeatureStampProviderName,
            tenantKey,
            providerState.Stamp,
            cancellationToken: cancellationToken);

        return new TenantEntitlementProvisioningResultDto
        {
            Outcome = TenantEntitlementProvisioningOutcome.Applied,
            SourceRevision = input.SourceRevision,
            ValuesChecksum = valuesChecksum,
            ProviderStamp = providerState.Stamp,
            AcknowledgedAtUtc = acknowledgedAtUtc
        };
    }

    internal static TenantEntitlementProvisioningOutcome? ClassifyReplay(
        Guid latestAggregateId,
        long latestRevision,
        DateTimeOffset latestEffectiveAtUtc,
        string latestChecksum,
        Guid incomingAggregateId,
        long incomingRevision,
        DateTimeOffset incomingEffectiveAtUtc,
        string incomingChecksum)
    {
        var sameAggregate = incomingAggregateId == latestAggregateId;
        var sameEffectiveTime = incomingEffectiveAtUtc == latestEffectiveAtUtc;
        var sameRevision = incomingRevision == latestRevision;
        var isOlder = sameAggregate
            ? incomingRevision < latestRevision || incomingEffectiveAtUtc < latestEffectiveAtUtc
            : incomingEffectiveAtUtc < latestEffectiveAtUtc;
        var isSame = sameAggregate && sameRevision && sameEffectiveTime;

        if ((!sameAggregate && sameEffectiveTime) ||
            (sameAggregate && sameRevision && !sameEffectiveTime))
        {
            throw new InvalidOperationException(
                "The entitlement ordering keys conflict with the latest applied snapshot.");
        }
        if (isSame && !string.Equals(incomingChecksum, latestChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The entitlement revision was already applied with a different payload.");
        }

        if (isSame) return TenantEntitlementProvisioningOutcome.Duplicate;
        if (isOlder) return TenantEntitlementProvisioningOutcome.Stale;
        return null;
    }

    private static string ComputeChecksum(IEnumerable<EntitlementValueDto> values)
    {
        var canonical = string.Join('\n', values
            .Select(x => (Name: x.Name.Trim(), Value: x.Value.Trim()))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => $"{x.Name}={x.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static List<string> Split(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
