using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Shared.EntityFrameworkCore.ChangeTrackers;

public class SharedEntityEntry
{
    public string Id { get; set; }

    public EntityEntry EntityEntry { get; set; }

    public List<SharedNavigationEntry> NavigationEntries { get; set; }

    private bool _isModified;
    public bool IsModified
    {
        get
        {
            return _isModified || EntityEntry.State == EntityState.Modified || NavigationEntries.Any(n => n.IsModified);
        }
        set => _isModified = value;
    }

    public SharedEntityEntry(string id, EntityEntry entityEntry)
    {
        Id = id;
        EntityEntry = entityEntry;
        NavigationEntries = EntityEntry.Navigations.Select(x => new SharedNavigationEntry(x, x.Metadata.Name)).ToList();
    }

    public void UpdateNavigation(EntityEntry entityEntry, SharedNavigationEntry navigationEntry)
    {
        if (IsModified ||
            EntityEntry.State == EntityState.Modified ||
            navigationEntry.IsModified)
        {
            return;
        }

        var currentValue = navigationEntry.NavigationEntry.CurrentValue;
        if (currentValue == null)
        {
            return;
        }

        if (navigationEntry.NavigationEntry is CollectionEntry)
        {
            navigationEntry.OriginalValue ??= new List<object>();
            ((List<object>)navigationEntry.OriginalValue).Add(entityEntry.Entity);
        }
        else
        {
            navigationEntry.OriginalValue = currentValue;
        }
    }
}

public class SharedNavigationEntry
{
    public NavigationEntry NavigationEntry { get; set; }

    public string Name { get; set; }

    public bool IsModified { get; set; }

    public object? OriginalValue { get; set; }

    public object? CurrentValue => NavigationEntry.CurrentValue;

    public SharedNavigationEntry(NavigationEntry navigationEntry, string name)
    {
        NavigationEntry = navigationEntry;
        Name = name;
        if (navigationEntry.CurrentValue != null)
        {
            OriginalValue = navigationEntry is CollectionEntry ? new List<object>() : navigationEntry.CurrentValue;
        }
    }
}
