using System;
using System.Collections.Generic;

namespace Shared.Domain.Entities;

public interface IEntitySelectorList : IList<EntitySelector>
{
}

public sealed record EntitySelector(Func<Type, bool> Predicate);

public class EntitySelectorList : List<EntitySelector>, IEntitySelectorList
{
}

public class SharedEntityChangeOptions
{
    public bool TrackNavigationChanges { get; set; } = true;

    public IEntitySelectorList IgnoredNavigationEntitySelectors { get; set; }

    public bool UpdateAggregateRootWhenNavigationChanges { get; set; } = true;

    public IEntitySelectorList IgnoredUpdateAggregateRootSelectors { get; set; }

    public SharedEntityChangeOptions()
    {
        IgnoredNavigationEntitySelectors = new EntitySelectorList();
        IgnoredUpdateAggregateRootSelectors = new EntitySelectorList();
    }
}
