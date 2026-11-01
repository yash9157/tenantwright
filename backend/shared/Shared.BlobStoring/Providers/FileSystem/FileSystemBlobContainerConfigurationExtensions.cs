namespace Shared.BlobStoring.Providers.FileSystem;

public static class FileSystemBlobContainerConfigurationExtensions
{
    public static FileSystemBlobProviderOptions GetFileSystemConfiguration(
        this BlobContainerConfiguration containerConfiguration)
    {
        ArgumentNullException.ThrowIfNull(containerConfiguration);
        return new FileSystemBlobProviderOptions(containerConfiguration);
    }

    public static BlobContainerConfiguration UseFileSystem(
        this BlobContainerConfiguration containerConfiguration,
        Action<FileSystemBlobProviderOptions>? configureAction = null)
    {
        ArgumentNullException.ThrowIfNull(containerConfiguration);

        containerConfiguration.ProviderType = typeof(FileSystemBlobProvider);
        containerConfiguration.TryAddNamingNormalizer<FileSystemBlobNamingNormalizer>();

        var options = new FileSystemBlobProviderOptions(containerConfiguration);
        configureAction?.Invoke(options);
        return containerConfiguration;
    }
}
