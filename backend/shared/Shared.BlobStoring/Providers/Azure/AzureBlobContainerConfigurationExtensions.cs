namespace Shared.BlobStoring.Providers.Azure;

public static class AzureBlobContainerConfigurationExtensions
{
    public static AzureBlobProviderOptions GetAzureConfiguration(this BlobContainerConfiguration containerConfiguration)
    {
        ArgumentNullException.ThrowIfNull(containerConfiguration);
        return new AzureBlobProviderOptions(containerConfiguration);
    }

    public static BlobContainerConfiguration UseAzure(
        this BlobContainerConfiguration containerConfiguration,
        Action<AzureBlobProviderOptions>? configureAction = null)
    {
        ArgumentNullException.ThrowIfNull(containerConfiguration);

        containerConfiguration.ProviderType = typeof(AzureBlobProvider);
        containerConfiguration.TryAddNamingNormalizer<AzureBlobNamingNormalizer>();

        var options = new AzureBlobProviderOptions(containerConfiguration);
        configureAction?.Invoke(options);
        return containerConfiguration;
    }
}
