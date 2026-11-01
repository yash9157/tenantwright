using System.Threading;

namespace Shared.BlobStoring;

public sealed class BlobProviderReadUrlArgs : BlobProviderArgs
{
    public TimeSpan Duration { get; }

    public BlobProviderReadUrlArgs(
        string containerName,
        BlobContainerConfiguration configuration,
        string blobName,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
        : base(containerName, configuration, blobName, cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new BlobConfigurationException("Signed URL duration must be greater than zero.");
        }

        Duration = duration;
    }
}
