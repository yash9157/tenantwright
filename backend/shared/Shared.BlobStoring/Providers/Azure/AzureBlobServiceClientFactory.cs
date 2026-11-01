using Azure.Storage.Blobs;
using System.Collections.Concurrent;

namespace Shared.BlobStoring.Providers.Azure;

public sealed class AzureBlobServiceClientFactory : IAzureBlobServiceClientFactory
{
    private readonly ConcurrentDictionary<string, BlobServiceClient> _clients = new(StringComparer.Ordinal);

    public BlobServiceClient GetClient(AzureBlobProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new BlobConfigurationException("Azure ConnectionString is required.");
        }

        return _clients.GetOrAdd(
            options.ConnectionString,
            connectionString => new BlobServiceClient(connectionString));
    }
}
