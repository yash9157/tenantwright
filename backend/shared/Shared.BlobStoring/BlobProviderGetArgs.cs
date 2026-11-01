using System.Threading;

namespace Shared.BlobStoring;

public sealed class BlobProviderGetArgs : BlobProviderArgs
{
    public BlobProviderGetArgs(
        string containerName,
        BlobContainerConfiguration configuration,
        string blobName,
        CancellationToken cancellationToken = default)
        : base(containerName, configuration, blobName, cancellationToken)
    {
    }
}
