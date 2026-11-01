using Microsoft.Extensions.Options;
using Shared.BlobStoring.Providers.Aws;
using Shared.BlobStoring.Providers.Azure;
using Shared.BlobStoring.Providers.FileSystem;

namespace Shared.BlobStoring;

public sealed class BlobStoringConfigurationValidator : IBlobStoringConfigurationValidator
{
    private readonly IOptionsMonitor<BlobStoringOptions> _optionsMonitor;

    public BlobStoringConfigurationValidator(IOptionsMonitor<BlobStoringOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
    }

    public void Validate(bool isDevelopment)
    {
        var options = _optionsMonitor.CurrentValue;
        var defaultConfiguration = options.Containers.GetConfiguration<DefaultContainer>();

        if (defaultConfiguration.ProviderType == null)
        {
            if (isDevelopment)
            {
                return;
            }

            throw new BlobConfigurationException(
                "BlobStoring default container provider is not configured. Configure BlobStoring:Containers:Default outside Development.");
        }

        foreach (var pair in options.Containers.GetAll())
        {
            ValidateContainer(pair.Key, pair.Value);
        }
    }

    private static void ValidateContainer(string containerName, BlobContainerConfiguration configuration)
    {
        if (configuration.ProviderType == null)
        {
            return;
        }

        if (configuration.ProviderType == typeof(FileSystemBlobProvider))
        {
            var fileSystemOptions = configuration.GetFileSystemConfiguration();
            if (string.IsNullOrWhiteSpace(fileSystemOptions.BasePath))
            {
                throw new BlobConfigurationException(
                    $"Container '{containerName}' uses FileSystem provider but BasePath is empty.");
            }

            return;
        }

        if (configuration.ProviderType == typeof(AwsBlobProvider))
        {
            var awsOptions = configuration.GetAwsConfiguration();
            if (string.IsNullOrWhiteSpace(awsOptions.Region))
            {
                throw new BlobConfigurationException(
                    $"Container '{containerName}' uses AWS provider but Region is empty.");
            }

            return;
        }

        if (configuration.ProviderType == typeof(AzureBlobProvider))
        {
            var azureOptions = configuration.GetAzureConfiguration();
            if (string.IsNullOrWhiteSpace(azureOptions.ConnectionString))
            {
                throw new BlobConfigurationException(
                    $"Container '{containerName}' uses Azure provider but ConnectionString is empty.");
            }

            return;
        }

        if (!typeof(IBlobProvider).IsAssignableFrom(configuration.ProviderType))
        {
            throw new BlobConfigurationException(
                $"Container '{containerName}' provider type '{configuration.ProviderType.FullName}' does not implement IBlobProvider.");
        }
    }
}
