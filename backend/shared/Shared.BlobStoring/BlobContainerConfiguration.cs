namespace Shared.BlobStoring;

public sealed class BlobContainerConfiguration
{
    public Type? ProviderType { get; set; }

    public bool IsMultiTenant { get; set; } = true;

    public IList<Type> NamingNormalizers { get; } = new List<Type>();

    private readonly Dictionary<string, object?> _properties;
    private readonly BlobContainerConfiguration? _fallbackConfiguration;

    public BlobContainerConfiguration(BlobContainerConfiguration? fallbackConfiguration = null)
    {
        _fallbackConfiguration = fallbackConfiguration;
        _properties = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public T? GetConfigurationOrDefault<T>(string name, T? defaultValue = default)
    {
        return (T?)GetConfigurationOrNull(name, defaultValue);
    }

    public T GetRequiredConfiguration<T>(string name)
    {
        var value = GetConfigurationOrNull(name);
        if (value is not T typedValue)
        {
            throw new BlobConfigurationException($"Missing required blob configuration value '{name}'.");
        }

        return typedValue;
    }

    public object? GetConfigurationOrNull(string name, object? defaultValue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_properties.TryGetValue(name, out var value))
        {
            return value;
        }

        return _fallbackConfiguration?.GetConfigurationOrNull(name, defaultValue) ?? defaultValue;
    }

    public BlobContainerConfiguration SetConfiguration(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (value == null)
        {
            _properties.Remove(name);
            return this;
        }

        _properties[name] = value;
        return this;
    }

    public BlobContainerConfiguration ClearConfiguration(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _properties.Remove(name);
        return this;
    }

    public BlobContainerConfiguration TryAddNamingNormalizer<TNormalizer>()
        where TNormalizer : class, IBlobNamingNormalizer
    {
        var normalizerType = typeof(TNormalizer);
        if (!NamingNormalizers.Contains(normalizerType))
        {
            NamingNormalizers.Add(normalizerType);
        }

        return this;
    }
}
