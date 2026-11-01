namespace Shared.BlobStoring.Providers.FileSystem;

public sealed class FileSystemBlobProviderOptions
{
    private readonly BlobContainerConfiguration _containerConfiguration;

    public FileSystemBlobProviderOptions(BlobContainerConfiguration containerConfiguration)
    {
        _containerConfiguration = containerConfiguration;
    }

    public string BasePath
    {
        get => _containerConfiguration.GetConfigurationOrDefault(
            FileSystemBlobProviderConfigurationNames.BasePath,
            Path.Combine(AppContext.BaseDirectory, "blobs"))!;
        set => _containerConfiguration.SetConfiguration(
            FileSystemBlobProviderConfigurationNames.BasePath,
            string.IsNullOrWhiteSpace(value)
                ? throw new BlobConfigurationException("FileSystem BasePath cannot be empty.")
                : value);
    }

    public bool AppendContainerNameToBasePath
    {
        get => _containerConfiguration.GetConfigurationOrDefault(
            FileSystemBlobProviderConfigurationNames.AppendContainerNameToBasePath,
            true);
        set => _containerConfiguration.SetConfiguration(
            FileSystemBlobProviderConfigurationNames.AppendContainerNameToBasePath,
            value);
    }

    public int RetryCount
    {
        get => Math.Max(0, _containerConfiguration.GetConfigurationOrDefault(
            FileSystemBlobProviderConfigurationNames.RetryCount,
            2));
        set => _containerConfiguration.SetConfiguration(
            FileSystemBlobProviderConfigurationNames.RetryCount,
            Math.Max(0, value));
    }

    public int RetryBaseDelayMs
    {
        get => Math.Max(1, _containerConfiguration.GetConfigurationOrDefault(
            FileSystemBlobProviderConfigurationNames.RetryBaseDelayMs,
            100));
        set => _containerConfiguration.SetConfiguration(
            FileSystemBlobProviderConfigurationNames.RetryBaseDelayMs,
            Math.Max(1, value));
    }
}
