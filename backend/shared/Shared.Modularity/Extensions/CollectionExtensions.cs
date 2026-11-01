using System;
using System.Collections.Generic;
using System.Linq;

namespace Shared.Modularity.Extensions;

public static class CollectionExtensions
{
    public static bool IsNullOrEmpty<T>(this ICollection<T>? source)
    {
        return source == null || source.Count <= 0;
    }

    public static bool AddIfNotContains<T>(this ICollection<T> source, T item)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Contains(item))
        {
            return false;
        }

        source.Add(item);
        return true;
    }

    public static IEnumerable<T> AddIfNotContains<T>(this ICollection<T> source, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(source);

        var addedItems = new List<T>();

        foreach (var item in items)
        {
            if (source.Contains(item))
            {
                continue;
            }

            source.Add(item);
            addedItems.Add(item);
        }

        return addedItems;
    }
}
