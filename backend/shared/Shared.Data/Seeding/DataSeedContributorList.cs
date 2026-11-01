using System;
using System.Collections.Generic;

namespace Shared.Data.Seeding;

public class DataSeedContributorList : List<Type>
{
    public void Add<T>() where T : IDataSeedContributor
    {
        AddIfNotContains(typeof(T));
    }

    public void AddIfNotContains(Type contributorType)
    {
        ArgumentNullException.ThrowIfNull(contributorType);

        if (!typeof(IDataSeedContributor).IsAssignableFrom(contributorType))
        {
            throw new InvalidOperationException(
                $"Type '{contributorType.FullName}' does not implement {nameof(IDataSeedContributor)}.");
        }

        if (!Contains(contributorType))
        {
            Add(contributorType);
        }
    }
}
