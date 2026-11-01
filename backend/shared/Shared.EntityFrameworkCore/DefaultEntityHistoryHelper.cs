using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Publishing;
using Shared.CurrentUser;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.EntityFrameworkCore.ChangeTrackers;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore;

public class DefaultEntityHistoryHelper : IEntityHistoryHelper, ITransientDependency
{
    private readonly ICurrentUser _currentUser;
    private readonly IAuditingHelper _auditingHelper;
    private readonly IAuditSerializer _auditSerializer;
    private readonly IOptions<SharedAuditingOptions> _auditingOptions;
    private SharedEfCoreNavigationHelper? _navigationHelper;

    public DefaultEntityHistoryHelper(
        ICurrentUser currentUser,
        ICurrentTenant currentTenant,
        IAuditingHelper auditingHelper,
        IAuditSerializer auditSerializer,
        IOptions<SharedAuditingOptions> auditingOptions)
    {
        _currentUser = currentUser;
        _auditingHelper = auditingHelper;
        _auditSerializer = auditSerializer;
        _auditingOptions = auditingOptions;
    }

    public void InitializeNavigationHelper(object navigationHelper)
    {
        _navigationHelper = navigationHelper as SharedEfCoreNavigationHelper;
    }

    public List<EntityChangeInfo> CreateChangeList(List<EntityEntry> entries)
    {
        var now = DateTime.UtcNow;
        var result = new List<EntityChangeInfo>();

        foreach (var entry in entries)
        {
            if (!ShouldSaveEntityHistory(entry))
            {
                continue;
            }

            var change = CreateEntityChangeOrNull(entry, now);
            if (change == null)
            {
                continue;
            }

            result.Add(change);
        }

        return result;
    }

    public void UpdateChangeList(List<EntityChangeInfo> entityChangeList)
    {
        foreach (var change in entityChangeList)
        {
            if (change.Entity is EntityEntry entityEntry)
            {
                change.ChangeTime = ResolveChangeTime(change.ChangeType, entityEntry.Entity);
                UpdateEntityKeys(change, entityEntry);
                ReconcileForeignKeyChanges(change, entityEntry);
                continue;
            }

            change.ChangeTime = ResolveChangeTime(change.ChangeType, change.Entity);
            UpdateEntityKeysFromEntity(change, change.Entity);
        }
    }

    private EntityChangeInfo? CreateEntityChangeOrNull(EntityEntry entry, DateTime changeTimeUtc)
    {
        var changeKind = DetermineChangeType(entry);
        if (changeKind == null)
        {
            return null;
        }

        var change = new EntityChangeInfo
        {
            EntityTypeFullName = ResolveEntityTypeFullName(entry),
            ChangeType = ToChangeTypeString(changeKind.Value),
            TenantId = ResolveEntityTenantId(entry.Entity),
            UserId = _currentUser.Id,
            ChangeTime = changeTimeUtc,
            Entity = entry
        };

        UpdateEntityKeys(change, entry);

        if (change.EntityId == null &&
            change.EntityKeys == null &&
            changeKind != EntityChangeKind.Created &&
            !EntityHelper.IsValueObject(entry.Entity))
        {
            return null;
        }

        change.PropertyChanges.AddRange(GetPropertyChanges(entry, changeKind.Value));
        return change;
    }

    private List<EntityPropertyChangeInfo> GetPropertyChanges(EntityEntry entry, EntityChangeKind changeKind)
    {
        var propertyChanges = new List<EntityPropertyChangeInfo>();
        var isCreated = changeKind == EntityChangeKind.Created;
        var isDeleted = changeKind == EntityChangeKind.Deleted;
        var isSoftDeleted = IsSoftDeleted(entry);

        foreach (var property in entry.Metadata.GetProperties())
        {
            if (entry.Metadata.IsMappedToJson() && property.GetJsonPropertyName() == null)
            {
                continue;
            }

            var propertyEntry = entry.Property(property.Name);
            if (ShouldSavePropertyHistory(propertyEntry, isCreated || isDeleted) && !isSoftDeleted)
            {
                var propertyType = DeterminePropertyTypeFromEntry(property, propertyEntry);
                propertyChanges.Add(new EntityPropertyChangeInfo
                {
                    PropertyName = Truncate(property.Name, AuditLogWriteRequestValidation.MaxPropertyNameLength) ?? string.Empty,
                    PropertyTypeFullName = Truncate(propertyType.FullName, AuditLogWriteRequestValidation.MaxPropertyTypeLength) ?? string.Empty,
                    OriginalValue = isCreated ? null : SerializePropertyValue(propertyEntry.OriginalValue),
                    NewValue = isDeleted ? null : SerializePropertyValue(propertyEntry.CurrentValue)
                });
            }
        }

        foreach (var complexPropertyEntry in entry.ComplexProperties)
        {
            AddComplexPropertyChanges(
                complexPropertyEntry,
                propertyChanges,
                isCreated,
                isDeleted,
                isSoftDeleted,
                parentPath: null);
        }

        if (_navigationHelper == null)
        {
            return propertyChanges;
        }

        foreach (var (navigationEntry, index) in entry.Navigations.Select((value, idx) => (value, idx)))
        {
            var propertyInfo = navigationEntry.Metadata.PropertyInfo;
            if (propertyInfo != null &&
                propertyInfo.IsDefined(typeof(DisableAuditingAttribute), true))
            {
                continue;
            }

            if (navigationEntry.Metadata.TargetEntityType.IsMappedToJson() &&
                navigationEntry is ReferenceEntry referenceEntry &&
                referenceEntry.TargetEntry != null)
            {
                foreach (var propertyChange in GetPropertyChanges(referenceEntry.TargetEntry, changeKind))
                {
                    propertyChange.PropertyName = Truncate(
                        $"{navigationEntry.Metadata.Name}.{propertyChange.PropertyName}",
                        AuditLogWriteRequestValidation.MaxPropertyNameLength) ?? string.Empty;
                    propertyChanges.Add(propertyChange);
                }

                continue;
            }

            if (!_navigationHelper.IsNavigationEntryModified(entry, index))
            {
                continue;
            }

            var trackedNavigation = _navigationHelper.GetNavigationEntry(entry, index);
            var isCollection = navigationEntry.Metadata.IsCollection;
            propertyChanges.Add(new EntityPropertyChangeInfo
            {
                PropertyName = Truncate(navigationEntry.Metadata.Name, AuditLogWriteRequestValidation.MaxPropertyNameLength) ?? string.Empty,
                PropertyTypeFullName = Truncate(
                    UnwrapNullableType(navigationEntry.Metadata.ClrType).FullName,
                    AuditLogWriteRequestValidation.MaxPropertyTypeLength) ?? string.Empty,
                OriginalValue = GetNavigationPropertyValue(trackedNavigation?.OriginalValue, isCollection),
                NewValue = GetNavigationPropertyValue(trackedNavigation?.CurrentValue, isCollection)
            });
        }

        return propertyChanges;
    }

    private void AddComplexPropertyChanges(
        ComplexPropertyEntry complexPropertyEntry,
        List<EntityPropertyChangeInfo> propertyChanges,
        bool isCreated,
        bool isDeleted,
        bool isSoftDeleted,
        string? parentPath)
    {
        var complexPropertyInfo = complexPropertyEntry.Metadata.PropertyInfo;
        if (complexPropertyInfo != null && complexPropertyInfo.IsDefined(typeof(DisableAuditingAttribute), true))
        {
            return;
        }

        var complexPropertyPath = parentPath == null
            ? complexPropertyEntry.Metadata.Name
            : $"{parentPath}.{complexPropertyEntry.Metadata.Name}";

        foreach (var propertyEntry in complexPropertyEntry.Properties)
        {
            if (ShouldSavePropertyHistory(propertyEntry, isCreated || isDeleted) && !isSoftDeleted)
            {
                var propertyType = DeterminePropertyTypeFromEntry(propertyEntry.Metadata, propertyEntry);
                propertyChanges.Add(new EntityPropertyChangeInfo
                {
                    PropertyName = Truncate(
                        $"{complexPropertyPath}.{propertyEntry.Metadata.Name}",
                        AuditLogWriteRequestValidation.MaxPropertyNameLength) ?? string.Empty,
                    PropertyTypeFullName = Truncate(
                        propertyType.FullName,
                        AuditLogWriteRequestValidation.MaxPropertyTypeLength) ?? string.Empty,
                    OriginalValue = isCreated ? null : SerializePropertyValue(propertyEntry.OriginalValue),
                    NewValue = isDeleted ? null : SerializePropertyValue(propertyEntry.CurrentValue)
                });
            }
        }

        foreach (var nestedComplexPropertyEntry in complexPropertyEntry.ComplexProperties)
        {
            AddComplexPropertyChanges(
                nestedComplexPropertyEntry,
                propertyChanges,
                isCreated,
                isDeleted,
                isSoftDeleted,
                complexPropertyPath);
        }
    }

    private bool ShouldSaveEntityHistory(EntityEntry entityEntry, bool defaultValue = false)
    {
        if (entityEntry.State == EntityState.Detached)
        {
            return false;
        }

        var entityType = entityEntry.Metadata.ClrType;
        if (!EntityHelper.IsEntity(entityType) && !EntityHelper.IsValueObject(entityType))
        {
            return false;
        }

        var isEntityHistoryEnabled = _auditingHelper.IsEntityHistoryEnabled(entityType);
        if (isEntityHistoryEnabled && HasNavigationPropertiesChanged(entityEntry))
        {
            return true;
        }

        return isEntityHistoryEnabled || defaultValue;
    }

    private static bool ShouldSavePropertyHistory(PropertyEntry propertyEntry, bool defaultValue)
    {
        if (string.Equals(propertyEntry.Metadata.Name, "Id", StringComparison.Ordinal))
        {
            return false;
        }

        var propertyInfo = propertyEntry.Metadata.PropertyInfo;
        if (propertyInfo != null && propertyInfo.IsDefined(typeof(DisableAuditingAttribute), true))
        {
            return false;
        }

        var entityType = propertyEntry.EntityEntry.Entity.GetType();
        if (entityType.IsDefined(typeof(DisableAuditingAttribute), true) &&
            (propertyInfo == null || !propertyInfo.IsDefined(typeof(AuditedAttribute), true)))
        {
            return false;
        }

        if (IsBaseAuditProperty(propertyEntry.Metadata.Name, entityType))
        {
            return false;
        }

        if (propertyEntry.OriginalValue is ExtraPropertyDictionary originalExtraProperties &&
            propertyEntry.CurrentValue is ExtraPropertyDictionary currentExtraProperties)
        {
            if (AreExtraPropertiesEqual(originalExtraProperties, currentExtraProperties))
            {
                return defaultValue;
            }

            return true;
        }

        var isModified = !(propertyEntry.OriginalValue?.Equals(propertyEntry.CurrentValue) ?? propertyEntry.CurrentValue == null);
        if (isModified)
        {
            return true;
        }

        return defaultValue;
    }

    private static bool IsBaseAuditProperty(string propertyName, Type entityType)
    {
        if (typeof(IHasCreationTime).IsAssignableFrom(entityType) &&
            string.Equals(propertyName, nameof(IHasCreationTime.CreationTime), StringComparison.Ordinal))
        {
            return true;
        }

        if (typeof(ICreationAuditedObject).IsAssignableFrom(entityType) &&
            string.Equals(propertyName, nameof(ICreationAuditedObject.CreatorId), StringComparison.Ordinal))
        {
            return true;
        }

        if (typeof(IHasModificationTime).IsAssignableFrom(entityType) &&
            string.Equals(propertyName, nameof(IHasModificationTime.LastModificationTime), StringComparison.Ordinal))
        {
            return true;
        }

        if (typeof(IModificationAuditedObject).IsAssignableFrom(entityType) &&
            string.Equals(propertyName, nameof(IModificationAuditedObject.LastModifierId), StringComparison.Ordinal))
        {
            return true;
        }

        if (typeof(ISoftDelete).IsAssignableFrom(entityType) &&
            string.Equals(propertyName, nameof(ISoftDelete.IsDeleted), StringComparison.Ordinal))
        {
            return true;
        }

        if (typeof(IHasDeletionTime).IsAssignableFrom(entityType) &&
            string.Equals(propertyName, nameof(IHasDeletionTime.DeletionTime), StringComparison.Ordinal))
        {
            return true;
        }

        if (typeof(IDeletionAuditedObject).IsAssignableFrom(entityType) &&
            string.Equals(propertyName, nameof(IDeletionAuditedObject.DeleterId), StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private EntityChangeKind? DetermineChangeType(EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
        {
            return EntityChangeKind.Created;
        }

        if (entry.State == EntityState.Deleted)
        {
            return EntityChangeKind.Deleted;
        }

        if (entry.State == EntityState.Modified)
        {
            return IsDeleted(entry) ? EntityChangeKind.Deleted : EntityChangeKind.Updated;
        }

        if (entry.State == EntityState.Unchanged && HasNavigationPropertiesChanged(entry))
        {
            return EntityChangeKind.Updated;
        }

        return null;
    }

    private bool HasNavigationPropertiesChanged(EntityEntry entityEntry)
    {
        return _auditingOptions.Value.SaveEntityHistoryWhenNavigationChanges &&
               _navigationHelper != null &&
               _navigationHelper.IsEntityEntryModified(entityEntry);
    }

    private static bool IsDeleted(EntityEntry entry)
    {
        return entry.State == EntityState.Deleted || IsSoftDeleted(entry);
    }

    private static bool IsSoftDeleted(EntityEntry entry)
    {
        return entry.Entity is ISoftDelete softDelete && softDelete.IsDeleted;
    }

    private static Guid? ResolveEntityTenantId(object entity)
    {
        return entity is IMultiTenant multiTenant ? multiTenant.TenantId : null;
    }

    private static string ResolveEntityTypeFullName(EntityEntry entityEntry)
    {
        if (entityEntry.Metadata.HasSharedClrType && !entityEntry.Metadata.IsOwned())
        {
            return TruncateFromBeginning(entityEntry.Metadata.Name, AuditLogWriteRequestValidation.MaxEntityTypeLength);
        }

        var fullName = entityEntry.Metadata.ClrType.FullName ?? entityEntry.Metadata.ClrType.Name;
        return TruncateFromBeginning(fullName, AuditLogWriteRequestValidation.MaxEntityTypeLength);
    }

    private static string ToChangeTypeString(EntityChangeKind changeKind)
    {
        return changeKind switch
        {
            EntityChangeKind.Created => "Created",
            EntityChangeKind.Deleted => "Deleted",
            _ => "Updated"
        };
    }

    private static EntityChangeKind ParseChangeKind(string? changeType)
    {
        if (string.Equals(changeType, "Created", StringComparison.OrdinalIgnoreCase))
        {
            return EntityChangeKind.Created;
        }

        if (string.Equals(changeType, "Deleted", StringComparison.OrdinalIgnoreCase))
        {
            return EntityChangeKind.Deleted;
        }

        return EntityChangeKind.Updated;
    }

    private static DateTime ResolveChangeTime(string? changeType, object? entity)
    {
        var kind = ParseChangeKind(changeType);
        var now = DateTime.UtcNow;

        return kind switch
        {
            EntityChangeKind.Created when entity is IHasCreationTime hasCreationTime && hasCreationTime.CreationTime != default =>
                NormalizeUtc(hasCreationTime.CreationTime),
            EntityChangeKind.Deleted when entity is IHasDeletionTime hasDeletionTime && hasDeletionTime.DeletionTime.HasValue =>
                NormalizeUtc(hasDeletionTime.DeletionTime.Value),
            EntityChangeKind.Updated when entity is IHasModificationTime hasModificationTime && hasModificationTime.LastModificationTime.HasValue =>
                NormalizeUtc(hasModificationTime.LastModificationTime.Value),
            _ => now
        };
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static void UpdateEntityKeys(EntityChangeInfo change, EntityEntry entityEntry)
    {
        var primaryKey = entityEntry.Metadata.FindPrimaryKey();
        if (primaryKey == null)
        {
            return;
        }

        var keyNames = primaryKey.Properties.Select(p => p.Name).ToList();
        change.KeyPropertyNames = keyNames;

        var keyValues = GetKeyValues(entityEntry, keyNames);
        ApplyEntityKeys(change, keyValues);
    }

    private static void UpdateEntityKeysFromEntity(EntityChangeInfo change, object? entity)
    {
        if (entity == null || change.KeyPropertyNames == null || change.KeyPropertyNames.Count == 0)
        {
            return;
        }

        var keyValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var keyName in change.KeyPropertyNames)
        {
            var property = entity.GetType().GetProperty(keyName);
            if (property == null)
            {
                continue;
            }

            keyValues[keyName] = ToEntityIdString(property.GetValue(entity));
        }

        ApplyEntityKeys(change, keyValues);
    }

    private static void ApplyEntityKeys(EntityChangeInfo change, Dictionary<string, string?> keyValues)
    {
        if (keyValues.Count == 1)
        {
            change.EntityId = keyValues.Values.FirstOrDefault();
            change.EntityKeys = null;
            return;
        }

        if (keyValues.Count > 1)
        {
            change.EntityId = null;
            change.EntityKeys = keyValues;
        }
    }

    private void ReconcileForeignKeyChanges(EntityChangeInfo change, EntityEntry entityEntry)
    {
        var foreignKeys = entityEntry.Metadata.GetForeignKeys();
        foreach (var foreignKey in foreignKeys)
        {
            foreach (var property in foreignKey.Properties)
            {
                var propertyEntry = entityEntry.Property(property.Name);
                var propertyChange = change.PropertyChanges.FirstOrDefault(pc =>
                    string.Equals(pc.PropertyName, property.Name, StringComparison.Ordinal));

                var originalValue = SerializePropertyValue(propertyEntry.OriginalValue);
                var currentValue = SerializePropertyValue(propertyEntry.CurrentValue);

                if (propertyChange == null)
                {
                    if (!string.Equals(originalValue, currentValue, StringComparison.Ordinal))
                    {
                        change.PropertyChanges.Add(new EntityPropertyChangeInfo
                        {
                            PropertyName = Truncate(property.Name, AuditLogWriteRequestValidation.MaxPropertyNameLength) ?? string.Empty,
                            PropertyTypeFullName = Truncate(
                                UnwrapNullableType(property.ClrType).FullName,
                                AuditLogWriteRequestValidation.MaxPropertyTypeLength) ?? string.Empty,
                            OriginalValue = originalValue,
                            NewValue = currentValue
                        });
                    }

                    continue;
                }

                if (!string.Equals(propertyChange.OriginalValue, propertyChange.NewValue, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(currentValue, propertyChange.NewValue, StringComparison.Ordinal))
                {
                    change.PropertyChanges.Remove(propertyChange);
                }
                else
                {
                    propertyChange.NewValue = currentValue;
                }
            }
        }
    }

    private static Dictionary<string, string?> GetKeyValues(EntityEntry entry, IReadOnlyList<string> keyNames)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var keyName in keyNames)
        {
            var property = entry.Property(keyName);
            result[keyName] = ToEntityIdString(property.CurrentValue);
        }

        return result;
    }

    private static Type DeterminePropertyTypeFromEntry(IReadOnlyPropertyBase property, PropertyEntry propertyEntry)
    {
        var propertyType = UnwrapNullableType(property.ClrType);
        if (propertyType != typeof(object))
        {
            return propertyType;
        }

        if (propertyEntry.CurrentValue != null)
        {
            return UnwrapNullableType(propertyEntry.CurrentValue.GetType());
        }

        if (propertyEntry.OriginalValue != null)
        {
            return UnwrapNullableType(propertyEntry.OriginalValue.GetType());
        }

        return propertyType;
    }

    private string? GetNavigationPropertyValue(object? entity, bool isCollection)
    {
        if (entity == null)
        {
            return null;
        }

        if (entity is IEntity navigationEntity)
        {
            var keys = navigationEntity.GetKeys();
            if (keys.Length == 0)
            {
                return null;
            }

            var serialized = string.Join(", ", keys.Select(ToEntityIdString));
            return Truncate(serialized, AuditLogWriteRequestValidation.MaxPropertyValueLength);
        }

        if (entity is IEnumerable enumerable)
        {
            var keys = new List<string>();
            foreach (var item in enumerable)
            {
                var id = GetNavigationPropertyValue(item, false);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    keys.Add(id);
                }
            }

            if (keys.Count == 0)
            {
                return null;
            }

            var serialized = keys.Count == 1 && !isCollection
                ? keys[0]
                : SerializePropertyValue(keys) ?? "null";

            return Truncate(serialized, AuditLogWriteRequestValidation.MaxPropertyValueLength);
        }

        return null;
    }

    private static bool AreExtraPropertiesEqual(ExtraPropertyDictionary left, ExtraPropertyDictionary right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var rightValue))
            {
                return false;
            }

            if (!Equals(pair.Value, rightValue))
            {
                return false;
            }
        }

        return true;
    }

    private string? SerializePropertyValue(object? value)
    {
        string? serialized;
        try
        {
            serialized = value == null ? "null" : _auditSerializer.Serialize(value);
        }
        catch
        {
            serialized = value == null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return Truncate(serialized, AuditLogWriteRequestValidation.MaxPropertyValueLength);
    }

    private static Type UnwrapNullableType(Type type)
    {
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    private static string? ToEntityIdString(object? value)
    {
        return value switch
        {
            null => null,
            DateTime dt => NormalizeUtc(dt).ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        const string postfix = "...";
        if (maxLength <= postfix.Length)
        {
            return value.Substring(0, maxLength);
        }

        return string.Concat(value.AsSpan(0, maxLength - postfix.Length), postfix);
    }

    private static string TruncateFromBeginning(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(value.Length - maxLength, maxLength);
    }
}
