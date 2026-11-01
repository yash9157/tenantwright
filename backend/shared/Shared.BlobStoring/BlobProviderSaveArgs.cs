using System.IO;
using System.Threading;

namespace Shared.BlobStoring;

public sealed class BlobProviderSaveArgs : BlobProviderArgs
{
    public Stream BlobStream { get; }

    public bool OverrideExisting { get; }

    public BlobProviderSaveArgs(
        string containerName,
        BlobContainerConfiguration configuration,
        string blobName,
        Stream blobStream,
        bool overrideExisting = false,
        CancellationToken cancellationToken = default)
        : base(containerName, configuration, blobName, cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobStream);
        BlobStream = blobStream;
        OverrideExisting = overrideExisting;
    }
}
