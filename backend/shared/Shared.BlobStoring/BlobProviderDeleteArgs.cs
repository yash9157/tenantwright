using System.Threading;

namespace Shared.BlobStoring;

public sealed class BlobProviderDeleteArgs : BlobProviderArgs
{
    public BlobProviderDeleteArgs(
        string containerName,
        BlobContainerConfiguration configuration,
        string blobName,
        CancellationToken cancellationToken = default)
        : base(containerName, configuration, blobName, cancellationToken)
    {
    }
}
