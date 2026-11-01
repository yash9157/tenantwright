using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Caching;
using Permissions.Application.Definitions;
using Permissions.Domain.FeatureManagement;
using Permissions.Domain.Shared.Constants;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.Data.Exceptions;
using Shared.EntityFrameworkCore;
using Shared.EventContracts;
using Shared.EventBus.Messaging;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Attributes;

using Shared.AuditLogs.Auditing;
namespace Permissions.Application.FeatureManagement;

public class FeatureManagementAppService : IFeatureManagementAppService, ITransientDependency
{
    private const string TenantProviderName = FeatureProviderNames.Tenant;
    private const string FeatureStampProviderName = "Feature";

    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly IRepository<FeatureDefinitionRecord, Guid> _definitionRepo;
    private readonly IRepository<FeatureGroupDefinitionRecord, Guid> _groupRepo;
    private readonly IRepository<FeatureValue, Guid> _valueRepo;
    private readonly IDbContextProvider<PermissionsDbContextApp> _dbContextProvider;
    private readonly FeatureProviderStateManager _providerStates;
    private readonly TenantFeatureCacheSyncService _cacheSync;
    private readonly PermissionStampService _stampService;
    private readonly ITenantWrightMessageSender _messageSender;
    private readonly FeatureDefinitionManager _definitions;

    public FeatureManagementAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        IRepository<FeatureDefinitionRecord, Guid> definitionRepo,
        IRepository<FeatureGroupDefinitionRecord, Guid> groupRepo,
        IRepository<FeatureValue, Guid> valueRepo,
        IDbContextProvider<PermissionsDbContextApp> dbContextProvider,
        FeatureProviderStateManager providerStates,
        TenantFeatureCacheSyncService cacheSync,
        PermissionStampService stampService,
        ITenantWrightMessageSender messageSender,
        FeatureDefinitionManager definitions)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _definitionRepo = definitionRepo;
        _groupRepo = groupRepo;
        _valueRepo = valueRepo;
        _dbContextProvider = dbContextProvider;
        _providerStates = providerStates;
        _cacheSync = cacheSync;
        _stampService = stampService;
        _messageSender = messageSender;
        _definitions = definitions;
    }

    [UnitOfWork(false)]
    public async Task<GetFeatureListResultDto> GetAsync(string providerName, string providerKey, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadFeatures, cancellationToken);
        providerName = NormalizeProviderName(providerName);
        providerKey = NormalizeProviderKey(providerKey);
        EnsureTenantProviderScope(providerName, providerKey);

        var groupQueryable = await _groupRepo.GetQueryableAsync();
        var defQueryable = await _definitionRepo.GetQueryableAsync();
        var valueQueryable = await _valueRepo.GetQueryableAsync();

        var groups = await groupQueryable
            .OrderBy(x => x.Name)
            .Select(x => new { x.Name, x.DisplayName })
            .ToListAsync(cancellationToken);

        var defs = await defQueryable
            .OrderBy(x => x.GroupName)
            .ThenBy(x => x.Name)
            .Select(x => new
            {
                x.GroupName,
                x.Name,
                x.DisplayName,
                x.DefaultValue,
                x.ValueType,
                x.Description,
                x.AllowedValues,
                x.MinValue,
                x.MaxValue,
                x.Unit,
                x.MutationPolicy,
                x.UsesAggregateTenantPool
            })
            .ToListAsync(cancellationToken);

        var values = await valueQueryable
            .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey)
            .Select(x => new { x.Name, x.Value })
            .ToListAsync(cancellationToken);

        var valueMap = values.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

        // Prerequisites live on the code-defined feature definitions (not the DB record), so map
        // feature name -> DependsOn once. The response can then render owner-defined prerequisite
        // rules from this single call without hardcoding a business capability in Permissions.
        var dependsOnMap = _definitions.GetGroups()
            .SelectMany(g => g.Features)
            .Where(f => f.DependsOn.Count > 0)
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DependsOn.ToList(), StringComparer.Ordinal);

        var groupMap = groups.ToDictionary(x => x.Name, x => new FeatureGroupDto
        {
            Name = x.Name,
            DisplayName = x.DisplayName
        }, StringComparer.Ordinal);

        foreach (var def in defs)
        {
            if (!groupMap.TryGetValue(def.GroupName, out var group))
            {
                group = new FeatureGroupDto { Name = def.GroupName, DisplayName = def.GroupName };
                groupMap.Add(def.GroupName, group);
            }

            var isOverridden = valueMap.TryGetValue(def.Name, out var storedValue);
            var value = storedValue ?? def.DefaultValue ?? "false";
            group.Features.Add(new FeatureDto
            {
                Name = def.Name,
                DisplayName = def.DisplayName,
                DefaultValue = def.DefaultValue ?? "false",
                Value = value,
                ValueType = string.IsNullOrWhiteSpace(def.ValueType) ? FeatureValueTypes.Toggle : def.ValueType,
                Description = def.Description,
                DependsOn = dependsOnMap.TryGetValue(def.Name, out var dependsOn) ? dependsOn : new List<string>(),
                IsOverridden = isOverridden,
                AllowedValues = SplitCsv(def.AllowedValues),
                MinValue = def.MinValue,
                MaxValue = def.MaxValue,
                Unit = def.Unit,
                MutationPolicy = def.MutationPolicy,
                UsesAggregateTenantPool = def.UsesAggregateTenantPool
            });
        }

        var stampTenantId = ResolveFeatureStampTenantId(providerName, providerKey);
        var providerState = await _providerStates.FindAsync(
            stampTenantId,
            FeatureStampProviderName,
            BuildFeatureStampProviderKey(stampTenantId),
            asNoTracking: true,
            cancellationToken: cancellationToken);
        var providerStamp = providerState?.Stamp ?? 0;

        return new GetFeatureListResultDto
        {
            ProviderName = providerName,
            ProviderKey = providerKey,
            ProviderStamp = providerStamp,
            Groups = groupMap.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList()
        };
    }


    [Audited]
    [UnitOfWork(true)]
    public async Task UpdateAsync(string providerName, string providerKey, UpdateFeaturesDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.UpdateFeatures, cancellationToken);
        providerName = NormalizeProviderName(providerName);
        providerKey = NormalizeProviderKey(providerKey);
        EnsureTenantProviderScope(providerName, providerKey);

        var stampTenantId = ResolveFeatureStampTenantId(providerName, providerKey);
        var stampProviderKey = BuildFeatureStampProviderKey(stampTenantId);
        var providerState = await _providerStates.FindAsync(
            stampTenantId,
            FeatureStampProviderName,
            stampProviderKey,
            cancellationToken: cancellationToken);
        var currentStamp = providerState?.Stamp ?? 0;
        if (input.ExpectedProviderStamp is { } expectedStamp && currentStamp != expectedStamp)
        {
            throw CreateConcurrencyConflict(currentStamp);
        }

        var updates = input.Features
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToArray();

        if (updates.Length == 0)
        {
            return;
        }

        var updateNames = updates.Select(i => i.Name.Trim()).ToArray();

        // Validate every submitted feature against its definition before writing anything, so an
        // invalid value in the batch aborts the whole update rather than half-applying.
        var defQueryable = await _definitionRepo.GetQueryableAsync();
        var definitions = await defQueryable
            .Where(x => updateNames.Contains(x.Name))
            .Select(x => new { x.Name, x.ValueType, x.AllowedValues, x.MinValue, x.MaxValue, x.AllowedProviders })
            .ToListAsync(cancellationToken);
        var definitionMap = definitions.ToDictionary(x => x.Name, StringComparer.Ordinal);

        var normalizedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in updates)
        {
            var name = item.Name.Trim();
            if (!definitionMap.TryGetValue(name, out var def))
            {
                throw new ArgumentException($"Unknown feature: {name}", nameof(input));
            }

            normalizedValues[name] = NormalizeFeatureValue(
                name, def.ValueType, (item.Value ?? string.Empty).Trim(), SplitCsv(def.AllowedValues), def.MinValue, def.MaxValue);
        }

        // Dependency checks must observe the complete resulting provider state, not only submitted
        // fields. Load every persisted value for this provider, overlay the normalized batch, and
        // validate before the first repository/cache/stamp/event mutation.
        var queryable = await _valueRepo.GetQueryableAsync();
        var existing = await queryable
            .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey)
            .ToListAsync(cancellationToken);
        var machineManagedUpdate = existing.FirstOrDefault(value =>
            value.ProvisioningSource is not null && updateNames.Contains(value.Name, StringComparer.Ordinal));
        if (machineManagedUpdate is not null)
        {
            throw new UnauthorizedAccessException(
                $"Feature '{machineManagedUpdate.Name}' is managed by '{machineManagedUpdate.ProvisioningSource}' and cannot be edited through the operator feature API.");
        }
        var persistedValues = existing.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

        ValidateFeatureDependencies(
            _definitions.GetGroups().SelectMany(group => group.Features),
            persistedValues,
            normalizedValues);

        var map = existing.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in updates)
        {
            var name = item.Name.Trim();
            var value = normalizedValues[name];

            if (map.TryGetValue(name, out var entity))
            {
                if (!string.Equals(entity.Value, value, StringComparison.Ordinal))
                {
                    entity.SetValue(value);
                    await _valueRepo.UpdateAsync(entity, autoSave: false, cancellationToken: cancellationToken);
                    changed.Add(name);
                }
            }
            else
            {
                await _valueRepo.InsertAsync(new FeatureValue(Guid.CreateVersion7(), name, value, providerName, providerKey, stampTenantId), autoSave: false, cancellationToken: cancellationToken);
                changed.Add(name);
            }
        }

        if (changed.Count > 0 && string.Equals(providerName, TenantProviderName, StringComparison.Ordinal))
        {
            providerState ??= await _providerStates.CreateAsync(
                stampTenantId,
                FeatureStampProviderName,
                stampProviderKey,
                cancellationToken);
            providerState.Advance(DateTime.UtcNow);

            // Flush the values and the provider's durable concurrency token together before any
            // cache or event work. A stale writer must fail here and roll back all of its values.
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw CreateConcurrencyConflict(providerState.Stamp);
            }
            catch (SharedDbConcurrencyException)
            {
                throw CreateConcurrencyConflict(providerState.Stamp);
            }
            catch (SharedConflictException exception) when (exception.Code is
                SharedErrorCodes.UniqueConstraintViolation or
                SharedErrorCodes.TransactionConflict or
                SharedErrorCodes.ConcurrencyConflict)
            {
                throw CreateConcurrencyConflict(providerState.Stamp);
            }

            await _cacheSync.SyncAsync(stampTenantId, changed, cancellationToken);

            // Publish the durable database stamp to the cache and invalidation stream. Cache
            // eviction or concurrent pods can no longer change optimistic-concurrency truth.
            await _stampService.BumpAsync(
                stampTenantId,
                FeatureStampProviderName,
                stampProviderKey,
                providerState.Stamp,
                cancellationToken: cancellationToken);

            if (stampTenantId.HasValue && stampTenantId.Value != Guid.Empty)
            {
                await _messageSender.PublishEventAsync(
                    new FeatureValuesChangedTenantEto
                    {
                        TenantId = stampTenantId.Value,
                        OccurredAtUtc = DateTimeOffset.UtcNow,
                        FeatureNames = changed.OrderBy(x => x, StringComparer.Ordinal).ToList()
                    }, cancellationToken);
            }
            else
            {
                await _messageSender.PublishEventAsync(
                    new FeatureValuesChangedHostEto
                    {
                        OccurredAtUtc = DateTimeOffset.UtcNow,
                        FeatureNames = changed.OrderBy(x => x, StringComparer.Ordinal).ToList()
                    }, cancellationToken);
            }
        }
    }

    internal static void ValidateFeatureDependencies(
        IEnumerable<Permissions.Application.Contracts.Definitions.FeatureDefinition> definitions,
        IReadOnlyDictionary<string, string> persistedValues,
        IReadOnlyDictionary<string, string> submittedValues)
    {
        var definitionMap = EnumerateFeatureDefinitions(definitions)
            .GroupBy(definition => definition.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var effectiveValues = definitionMap.Values.ToDictionary(
            definition => definition.Name,
            definition => definition.DefaultValue ?? "false",
            StringComparer.Ordinal);

        foreach (var (name, value) in persistedValues)
        {
            if (definitionMap.ContainsKey(name))
            {
                effectiveValues[name] = value;
            }
        }
        foreach (var (name, value) in submittedValues)
        {
            if (definitionMap.ContainsKey(name))
            {
                effectiveValues[name] = value;
            }
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var definition in definitionMap.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (definition.DependsOn.Count == 0 || !IsEnabledToggle(definition, effectiveValues[definition.Name]))
            {
                continue;
            }

            var unmetPrerequisites = definition.DependsOn
                .Where(prerequisiteName =>
                    !definitionMap.TryGetValue(prerequisiteName, out var prerequisite) ||
                    !IsEnabledToggle(
                        prerequisite,
                        effectiveValues.TryGetValue(prerequisiteName, out var prerequisiteValue)
                            ? prerequisiteValue
                            : prerequisite.DefaultValue))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (unmetPrerequisites.Length > 0)
            {
                errors[definition.Name] = unmetPrerequisites;
            }
        }

        if (errors.Count > 0)
        {
            throw new Shared.SharedValidationException(
                PermissionsErrorCodes.FeatureDependencyNotSatisfied,
                "One or more enabled features require other features to be enabled.",
                errors);
        }
    }

    private static bool IsEnabledToggle(
        Permissions.Application.Contracts.Definitions.FeatureDefinition definition,
        string? value)
    {
        return string.Equals(definition.ValueType, FeatureValueTypes.Toggle, StringComparison.Ordinal) &&
            (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }

    private static IEnumerable<Permissions.Application.Contracts.Definitions.FeatureDefinition> EnumerateFeatureDefinitions(
        IEnumerable<Permissions.Application.Contracts.Definitions.FeatureDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            yield return definition;
            foreach (var child in EnumerateFeatureDefinitions(definition.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>Feature stamps are keyed by tenant: the tenant provider key is the tenant GUID, or
    /// null for the host scope.</summary>
    private static Guid? ResolveFeatureStampTenantId(string providerName, string providerKey)
    {
        if (!string.Equals(providerName, TenantProviderName, StringComparison.Ordinal))
        {
            return null;
        }

        return string.Equals(providerKey, "host", StringComparison.Ordinal) ? null : Guid.Parse(providerKey);
    }

    [UnitOfWork(false)]
    public async Task<BatchFeatureCheckResponseDto> BatchCheckAsync(BatchFeatureCheckRequestDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.CheckFeaturesInternal, cancellationToken);
        var tenantId = ResolveTenantScope(input.TenantId);
        var providerName = TenantProviderName;
        var providerKey = tenantId?.ToString("D") ?? "host";

        var names = input.FeatureNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
        {
            return new BatchFeatureCheckResponseDto();
        }

        var defQueryable = await _definitionRepo.GetQueryableAsync();
        var defaults = await defQueryable
            .Where(x => names.Contains(x.Name))
            .Select(x => new { x.Name, x.DefaultValue })
            .ToListAsync(cancellationToken);

        var defaultMap = defaults.ToDictionary(x => x.Name, x => x.DefaultValue ?? "false", StringComparer.Ordinal);

        var valueQueryable = await _valueRepo.GetQueryableAsync();
        var values = await valueQueryable
            .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey && names.Contains(x.Name))
            .Select(x => new { x.Name, x.Value })
            .ToListAsync(cancellationToken);

        var valueMap = values.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = valueMap.TryGetValue(name, out var v)
                ? v
                : (defaultMap.TryGetValue(name, out var dv) ? dv : "false");
            result[name] = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        return new BatchFeatureCheckResponseDto { Results = result };
    }

    private Guid? ResolveTenantScope(Guid? requestedTenantId)
    {
        var currentTenantId = _currentTenant.Id;
        if (!currentTenantId.HasValue)
        {
            return requestedTenantId;
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != currentTenantId.Value)
        {
            throw new UnauthorizedAccessException("Cross-tenant feature access is not allowed.");
        }

        return currentTenantId;
    }

    private void EnsureTenantProviderScope(string providerName, string providerKey)
    {
        if (!string.Equals(providerName, TenantProviderName, StringComparison.Ordinal))
        {
            return;
        }

        var currentTenantId = _currentTenant.Id;
        if (!currentTenantId.HasValue)
        {
            return;
        }

        var expected = currentTenantId.Value.ToString("D");
        if (!string.Equals(providerKey, expected, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Cross-tenant feature update is not allowed.");
        }
    }

    private static string NormalizeProviderName(string providerName)
    {
        providerName = providerName.Trim();
        if (string.Equals(providerName, FeatureProviderNames.Tenant, StringComparison.OrdinalIgnoreCase)) return FeatureProviderNames.Tenant;
        throw new ArgumentException("Unsupported feature provider.", nameof(providerName));
    }

    private static string NormalizeProviderKey(string providerKey)
    {
        return providerKey.Trim();
    }

    private static string BuildFeatureStampProviderKey(Guid? tenantId)
    {
        return tenantId?.ToString("D") ?? "host";
    }

    private static FeatureConcurrencyConflictException CreateConcurrencyConflict(long currentStamp)
    {
        return new FeatureConcurrencyConflictException(new FeatureConcurrencyConflictDto
        {
            Code = "feature-stamp-mismatch",
            Message = "Feature values changed since they were loaded.",
            CurrentStamp = currentStamp
        });
    }

    /// <summary>
    /// Validates and canonicalizes a submitted value against its definition value type. Throws
    /// <see cref="ArgumentException"/> (surfaced as 400) when the value is not valid for the type.
    /// </summary>
    internal static string NormalizeFeatureValue(
        string name,
        string valueType,
        string value,
        IReadOnlyCollection<string>? allowedValues = null,
        decimal? minValue = null,
        decimal? maxValue = null)
    {
        var type = string.IsNullOrWhiteSpace(valueType) ? FeatureValueTypes.Toggle : valueType.Trim();
        switch (type)
        {
            case FeatureValueTypes.Toggle:
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1")
                {
                    return "true";
                }
                if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0" || value.Length == 0)
                {
                    return "false";
                }
                throw new ArgumentException($"Feature '{name}' is a toggle and only accepts true/false.", nameof(value));

            case FeatureValueTypes.Number:
                if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    throw new ArgumentException($"Feature '{name}' is numeric and requires a number.", nameof(value));
                }
                if (minValue.HasValue && number < minValue.Value)
                {
                    throw new ArgumentException(
                        $"Feature '{name}' must be at least {minValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}.", nameof(value));
                }
                if (maxValue.HasValue && number > maxValue.Value)
                {
                    throw new ArgumentException(
                        $"Feature '{name}' must be at most {maxValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}.", nameof(value));
                }
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture);

            case FeatureValueTypes.Selection:
                // An empty value clears the override so the feature falls back to its default on read.
                if (value.Length == 0)
                {
                    return value;
                }
                if (allowedValues is { Count: > 0 } && !allowedValues.Contains(value, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"Feature '{name}' only accepts one of: {string.Join(", ", allowedValues)}.", nameof(value));
                }
                return value;

            case FeatureValueTypes.FreeText:
                return value;

            default:
                // Unknown definition type: accept the raw value rather than silently corrupting it.
                return value;
        }
    }

    /// <summary>Splits a stored comma-separated definition value (allowed selection values) into a list.</summary>
    private static List<string> SplitCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
    }
}
