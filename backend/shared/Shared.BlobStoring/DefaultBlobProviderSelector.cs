using Microsoft.Extensions.DependencyInjection;

namespace Shared.BlobStoring;

public sealed class DefaultBlobProviderSelector : IBlobProviderSelector
{
    private readonly IBlobContainerConfigurationProvider _configurationProvider;
    private readonly IServiceProvider _serviceProvider;

    public DefaultBlobProviderSelector(
        IBlobContainerConfigurationProvider configurationProvider,
        IServiceProvider serviceProvider)
    {
        _configurationProvider = configurationProvider;
        _serviceProvider = serviceProvider;
    }

    public IBlobProvider Get(string containerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var configuration = _configurationProvider.Get(containerName);
        if (configuration.ProviderType == null)
        {
            throw new BlobConfigurationException(
                $"No provider configured for blob container '{containerName}'.");
        }

        if (!typeof(IBlobProvider).IsAssignableFrom(configuration.ProviderType))
        {
            throw new BlobConfigurationException(
                $"Configured provider type '{configuration.ProviderType.FullName}' is not an IBlobProvider.");
        }

        return (IBlobProvider)_serviceProvider.GetRequiredService(configuration.ProviderType);
    }
}
