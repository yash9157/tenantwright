using System;
namespace Shared.Data.Filtering;

public class SharedDataFilterOptions
{
    public Dictionary<Type, DataFilterState> DefaultStates { get; }

    public SharedDataFilterOptions()
    {
        DefaultStates = new Dictionary<Type, DataFilterState>();
    }
}
