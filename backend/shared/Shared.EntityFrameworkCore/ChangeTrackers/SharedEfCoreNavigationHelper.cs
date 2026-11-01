using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Shared.Modularity.DependencyInjection;
using Shared.Domain.Entities;

namespace Shared.EntityFrameworkCore.ChangeTrackers;

/// <summary>
/// Refactor this class after EF Core supports this case.
/// https://github.com/dotnet/efcore/issues/24076#issuecomment-1996623874
/// </summary>
public class SharedEfCoreNavigationHelper : ITransientDependency
{
    protected Dictionary<string, SharedEntityEntry> EntityEntries { get; } = new();

    public virtual void ChangeTrackerTracked(object? sender, EntityTrackedEventArgs e)
    {
        EntityEntryTrackedOrStateChanged(e.Entry);
        DetectChanges(e.Entry);
    }

    public virtual void ChangeTrackerStateChanged(object? sender, EntityStateChangedEventArgs e)
    {
        EntityEntryTrackedOrStateChanged(e.Entry);
        DetectChanges(e.Entry);
    }

    protected virtual void EntityEntryTrackedOrStateChanged(EntityEntry entityEntry)
    {
        if (entityEntry.State != EntityState.Unchanged)
        {
            return;
        }

        var entryId = GetEntityEntryIdentity(entityEntry);
        if (entryId == null)
        {
            return;
        }

        if (EntityEntries.ContainsKey(entryId))
        {
            return;
        }

        EntityEntries.Add(entryId, new SharedEntityEntry(entryId, entityEntry));
    }

    protected virtual void DetectChanges(EntityEntry entityEntry, bool checkEntityEntryState = true)
    {
#pragma warning disable EF1001
        var stateManager = entityEntry.Context.GetDependencies().StateManager;
        var internalEntityEntityEntry = stateManager.TryGetEntry(entityEntry.Entity, throwOnNonUniqueness: false);
        if (internalEntityEntityEntry == null)
        {
            return;
        }

        var foreignKeys = entityEntry.Metadata.GetForeignKeys();
        foreach (var foreignKey in foreignKeys)
        {
            var principal = stateManager.FindPrincipal(internalEntityEntityEntry, foreignKey);
            if (principal == null)
            {
                continue;
            }

            var entryId = GetEntityEntryIdentity(principal.ToEntityEntry());
            if (entryId == null || !EntityEntries.TryGetValue(entryId, out var sharedEntityEntry))
            {
                continue;
            }

            var navigationEntry = sharedEntityEntry.NavigationEntries.FirstOrDefault(x => x.NavigationEntry.Metadata is INavigation navigationMetadata && navigationMetadata.ForeignKey == foreignKey) ??
                                  sharedEntityEntry.NavigationEntries.FirstOrDefault(x => x.NavigationEntry.Metadata is ISkipNavigation skipNavigationMetadata && skipNavigationMetadata.ForeignKey == foreignKey);

            if (navigationEntry != null && checkEntityEntryState && entityEntry.State == EntityState.Unchanged)
            {
                sharedEntityEntry.UpdateNavigation(entityEntry, navigationEntry);
            }

            if (!sharedEntityEntry.IsModified && (!checkEntityEntryState || IsEntityEntryChanged(entityEntry)))
            {
                sharedEntityEntry.IsModified = true;
                DetectChanges(sharedEntityEntry.EntityEntry, false);
            }

            if (navigationEntry != null && IsEntityEntryChanged(entityEntry))
            {
                navigationEntry.IsModified = true;
            }
        }

        var skipNavigations = entityEntry.Metadata.GetSkipNavigations().ToList();
        foreach (var skipNavigation in skipNavigations)
        {
            var joinEntityType = skipNavigation.JoinEntityType;
            var foreignKey = skipNavigation.ForeignKey;
            var inverseForeignKey = skipNavigation.Inverse.ForeignKey;
            foreach (var joinEntry in stateManager.Entries)
            {
                if (joinEntry.EntityType != joinEntityType || stateManager.FindPrincipal(joinEntry, foreignKey) != internalEntityEntityEntry)
                {
                    continue;
                }

                var principal = stateManager.FindPrincipal(joinEntry, inverseForeignKey);
                if (principal == null)
                {
                    continue;
                }

                var entryId = GetEntityEntryIdentity(principal.ToEntityEntry());
                if (entryId == null || !EntityEntries.TryGetValue(entryId, out var sharedEntityEntry))
                {
                    continue;
                }

                var navigationEntry = sharedEntityEntry.NavigationEntries.FirstOrDefault(x => x.NavigationEntry.Metadata is INavigation navigationMetadata && navigationMetadata.ForeignKey == inverseForeignKey) ??
                                      sharedEntityEntry.NavigationEntries.FirstOrDefault(x => x.NavigationEntry.Metadata is ISkipNavigation skipNavigationMetadata && skipNavigationMetadata.ForeignKey == inverseForeignKey);

                if (navigationEntry != null && checkEntityEntryState && entityEntry.State == EntityState.Unchanged)
                {
                    sharedEntityEntry.UpdateNavigation(entityEntry, navigationEntry);
                }

                if (!sharedEntityEntry.IsModified && (!checkEntityEntryState || IsEntityEntryChanged(entityEntry)))
                {
                    sharedEntityEntry.IsModified = true;
                    DetectChanges(sharedEntityEntry.EntityEntry, false);
                }

                if (navigationEntry != null && (!checkEntityEntryState || IsEntityEntryChanged(entityEntry)))
                {
                    navigationEntry.IsModified = true;
                }
            }
        }
#pragma warning restore EF1001
    }

    protected virtual bool IsEntityEntryChanged(EntityEntry entityEntry)
    {
        return entityEntry.State == EntityState.Added ||
               entityEntry.State == EntityState.Deleted ||
               entityEntry.State == EntityState.Modified;
    }

    public virtual List<EntityEntry> GetChangedEntityEntries()
    {
        return EntityEntries
            .Where(x => x.Value.IsModified)
            .Select(x => x.Value.EntityEntry)
            .ToList();
    }

    public virtual bool IsEntityEntryModified(EntityEntry entityEntry)
    {
        if (entityEntry.State == EntityState.Modified)
        {
            return true;
        }

        var entryId = GetEntityEntryIdentity(entityEntry);
        if (entryId == null)
        {
            return false;
        }

        return EntityEntries.TryGetValue(entryId, out var sharedEntityEntry) && sharedEntityEntry.IsModified;
    }

    public virtual bool IsNavigationEntryModified(EntityEntry entityEntry, int? navigationEntryIndex = null)
    {
        var entryId = GetEntityEntryIdentity(entityEntry);
        if (entryId == null)
        {
            return false;
        }

        if (!EntityEntries.TryGetValue(entryId, out var sharedEntityEntry))
        {
            return false;
        }

        if (navigationEntryIndex == null)
        {
            return sharedEntityEntry.NavigationEntries.Any(x => x.IsModified);
        }

        var navigationEntryProperty = sharedEntityEntry.NavigationEntries.ElementAtOrDefault(navigationEntryIndex.Value);
        return navigationEntryProperty != null && navigationEntryProperty.IsModified;
    }

    public virtual SharedNavigationEntry? GetNavigationEntry(EntityEntry entityEntry, int navigationEntryIndex)
    {
        var entryId = GetEntityEntryIdentity(entityEntry);
        if (entryId == null)
        {
            return null;
        }

        if (!EntityEntries.TryGetValue(entryId, out var sharedEntityEntry))
        {
            return null;
        }

        return sharedEntityEntry.NavigationEntries.ElementAtOrDefault(navigationEntryIndex);
    }

    protected virtual string? GetEntityEntryIdentity(EntityEntry entityEntry)
    {
        if (entityEntry.Entity is IEntity entryEntity)
        {
            var keys = entryEntity.GetKeys();
            if (keys.Length == 0 || keys.All(x => x == null))
            {
                return null;
            }

            var keyIdentity = string.Join("|", keys.Select(SerializeKeyPart));
            var entityTypeName = entityEntry.Metadata.ClrType.FullName ?? entityEntry.Metadata.ClrType.Name;
            return $"{entityTypeName}:{keyIdentity}";
        }

        return null;
    }

    private static string SerializeKeyPart(object? value)
    {
        return value switch
        {
            null => "null",
            DateTime dateTime => dateTime.Kind == DateTimeKind.Utc
                ? dateTime.ToString("O", CultureInfo.InvariantCulture)
                : dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
        };
    }

    public virtual void RemoveChangedEntityEntries()
    {
        var keysToRemove = EntityEntries.Where(x => x.Value.IsModified).Select(x => x.Key).ToList();
        foreach (var key in keysToRemove)
        {
            EntityEntries.Remove(key);
        }
    }

    public virtual void Clear()
    {
        EntityEntries.Clear();
    }
}
