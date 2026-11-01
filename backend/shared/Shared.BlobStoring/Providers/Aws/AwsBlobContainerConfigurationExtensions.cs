namespace Shared.BlobStoring.Providers.Aws;

public static class AwsBlobContainerConfigurationExtensions
{
    public static AwsBlobProviderOptions GetAwsConfiguration(this BlobContainerConfiguration containerConfiguration)
    {
        ArgumentNullException.ThrowIfNull(containerConfiguration);
        return new AwsBlobProviderOptions(containerConfiguration);
    }

    public static BlobContainerConfiguration UseAws(
        this BlobContainerConfiguration containerConfiguration,
        Action<AwsBlobProviderOptions>? configureAction = null)
    {
        ArgumentNullException.ThrowIfNull(containerConfiguration);

        containerConfiguration.ProviderType = typeof(AwsBlobProvider);
        containerConfiguration.TryAddNamingNormalizer<AwsBlobNamingNormalizer>();

        var options = new AwsBlobProviderOptions(containerConfiguration);
        configureAction?.Invoke(options);
        return containerConfiguration;
    }
}
