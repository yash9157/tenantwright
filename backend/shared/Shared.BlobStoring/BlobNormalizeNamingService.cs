using Microsoft.Extensions.DependencyInjection;

namespace Shared.BlobStoring;

public sealed class BlobNormalizeNamingService : IBlobNormalizeNamingService
{
    private readonly IServiceProvider _serviceProvider;

    public BlobNormalizeNamingService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public BlobNormalizeNaming NormalizeNaming(
        BlobContainerConfiguration configuration,
        string? containerName,
        string? blobName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.NamingNormalizers.Count == 0)
        {
            return new BlobNormalizeNaming(containerName, blobName);
        }

        using var scope = _serviceProvider.CreateScope();
        foreach (var normalizerType in configuration.NamingNormalizers)
        {
            var normalizer = (IBlobNamingNormalizer)scope.ServiceProvider.GetRequiredService(normalizerType);

            if (!string.IsNullOrWhiteSpace(containerName))
            {
                containerName = normalizer.NormalizeContainerName(containerName);
            }

            if (!string.IsNullOrWhiteSpace(blobName))
            {
                blobName = normalizer.NormalizeBlobName(blobName);
            }
        }

        return new BlobNormalizeNaming(containerName, blobName);
    }
}
