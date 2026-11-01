using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Shared.Domain;

namespace Shared.EntityFrameworkCore.ValueComparers;

public class ExtraPropertyDictionaryValueComparer : ValueComparer<ExtraPropertyDictionary>
{
    public ExtraPropertyDictionaryValueComparer()
        : base(
            (left, right) => AreEqual(left, right),
            dictionary => GetStableHashCode(dictionary),
            dictionary => dictionary == null ? null! : new ExtraPropertyDictionary(dictionary))
    {
    }

    private static bool AreEqual(ExtraPropertyDictionary? left, ExtraPropertyDictionary? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var kvp in left)
        {
            if (!right.TryGetValue(kvp.Key, out var otherValue))
            {
                return false;
            }

            if (!object.Equals(kvp.Value, otherValue))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetStableHashCode(ExtraPropertyDictionary? dictionary)
    {
        if (dictionary == null)
        {
            return 0;
        }

        var hash = new HashCode();
        foreach (var key in dictionary.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(dictionary[key]);
        }

        return hash.ToHashCode();
    }
}
