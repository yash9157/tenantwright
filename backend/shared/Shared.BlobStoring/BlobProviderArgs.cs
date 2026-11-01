using System.Threading;

namespace Shared.BlobStoring;

public abstract class BlobProviderArgs
{
    public string ContainerName { get; }

    public BlobContainerConfiguration Configuration { get; }

    public string BlobName { get; }

    public CancellationToken CancellationToken { get; }

    protected BlobProviderArgs(
        string containerName,
        BlobContainerConfiguration configuration,
        string blobName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        ContainerName = containerName;
        Configuration = configuration;
        BlobName = blobName;
        CancellationToken = cancellationToken;
    }
}
