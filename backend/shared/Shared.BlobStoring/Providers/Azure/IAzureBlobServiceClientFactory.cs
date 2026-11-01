using Azure.Storage.Blobs;

namespace Shared.BlobStoring.Providers.Azure;

public interface IAzureBlobServiceClientFactory
{
    BlobServiceClient GetClient(AzureBlobProviderOptions options);
}
