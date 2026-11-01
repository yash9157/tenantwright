namespace Shared.BlobStoring.Providers.Azure;

public sealed class AzureBlobProviderOptions
{
    private readonly BlobContainerConfiguration _containerConfiguration;

    public AzureBlobProviderOptions(BlobContainerConfiguration containerConfiguration)
    {
        _containerConfiguration = containerConfiguration;
    }

    public string ConnectionString
    {
        get => _containerConfiguration.GetConfigurationOrDefault(
                AzureBlobProviderConfigurationNames.ConnectionString,
                string.Empty) ?? string.Empty;
        set => _containerConfiguration.SetConfiguration(
            AzureBlobProviderConfigurationNames.ConnectionString,
            string.IsNullOrWhiteSpace(value)
                ? throw new BlobConfigurationException("Azure ConnectionString cannot be empty.")
                : value);
    }

    public string? ContainerName
    {
        get => _containerConfiguration.GetConfigurationOrDefault<string>(AzureBlobProviderConfigurationNames.ContainerName);
        set => _containerConfiguration.SetConfiguration(AzureBlobProviderConfigurationNames.ContainerName, value);
    }

    public bool CreateContainerIfNotExists
    {
        get => _containerConfiguration.GetConfigurationOrDefault(
            AzureBlobProviderConfigurationNames.CreateContainerIfNotExists,
            false);
        set => _containerConfiguration.SetConfiguration(AzureBlobProviderConfigurationNames.CreateContainerIfNotExists, value);
    }
}
