namespace Shared.Modularity.DependencyInjection;

public sealed class SingletonNormalizationDiagnostics
{
    private readonly List<SingletonNormalizationRecord> _records = new();

    public IReadOnlyList<SingletonNormalizationRecord> Records => _records;

    public void Record(Type serviceType, int replacedUnkeyedDescriptors, int preservedKeyedDescriptors)
    {
        _records.Add(new SingletonNormalizationRecord(
            serviceType.FullName ?? serviceType.Name,
            replacedUnkeyedDescriptors,
            preservedKeyedDescriptors));
    }
}

public readonly record struct SingletonNormalizationRecord(
    string ServiceType,
    int ReplacedUnkeyedDescriptors,
    int PreservedKeyedDescriptors);
