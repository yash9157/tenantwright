namespace Shared.BlobStoring;

public interface IBlobNormalizeNamingService
{
    BlobNormalizeNaming NormalizeNaming(
        BlobContainerConfiguration configuration,
        string? containerName,
        string? blobName);
}
