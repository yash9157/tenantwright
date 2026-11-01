namespace Shared.BlobStoring.Providers.FileSystem;

public static class FileSystemBlobProviderConfigurationNames
{
    public const string BasePath = "FileSystem.BasePath";
    public const string AppendContainerNameToBasePath = "FileSystem.AppendContainerNameToBasePath";
    public const string RetryCount = "FileSystem.RetryCount";
    public const string RetryBaseDelayMs = "FileSystem.RetryBaseDelayMs";
}
