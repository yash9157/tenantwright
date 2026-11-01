namespace Shared.BlobStoring;

public sealed class BlobContainerConfigurations
{
    private readonly Dictionary<string, BlobContainerConfiguration> _containers;

    private BlobContainerConfiguration Default => GetConfiguration<DefaultContainer>();

    public BlobContainerConfigurations()
    {
        _containers = new Dictionary<string, BlobContainerConfiguration>(StringComparer.Ordinal)
        {
            [BlobContainerNameAttribute.GetContainerName<DefaultContainer>()] = new BlobContainerConfiguration()
        };
    }

    public BlobContainerConfigurations Configure<TContainer>(Action<BlobContainerConfiguration> configureAction)
    {
        return Configure(BlobContainerNameAttribute.GetContainerName<TContainer>(), configureAction);
    }

    public BlobContainerConfigurations Configure(string name, Action<BlobContainerConfiguration> configureAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configureAction);

        if (!_containers.TryGetValue(name, out var container))
        {
            container = new BlobContainerConfiguration(Default);
            _containers[name] = container;
        }

        configureAction(container);
        return this;
    }

    public BlobContainerConfigurations ConfigureDefault(Action<BlobContainerConfiguration> configureAction)
    {
        ArgumentNullException.ThrowIfNull(configureAction);
        configureAction(Default);
        return this;
    }

    public BlobContainerConfigurations ConfigureAll(Action<string, BlobContainerConfiguration> configureAction)
    {
        ArgumentNullException.ThrowIfNull(configureAction);

        foreach (var pair in _containers)
        {
            configureAction(pair.Key, pair.Value);
        }

        return this;
    }

    public BlobContainerConfiguration GetConfiguration<TContainer>()
    {
        return GetConfiguration(BlobContainerNameAttribute.GetContainerName<TContainer>());
    }

    public BlobContainerConfiguration GetConfiguration(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _containers.GetValueOrDefault(name) ?? Default;
    }

    public IReadOnlyDictionary<string, BlobContainerConfiguration> GetAll()
    {
        return _containers;
    }
}
