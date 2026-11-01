using System.Text.RegularExpressions;

namespace Shared.BlobStoring.Providers.Azure;

public sealed class AzureBlobNamingNormalizer : IBlobNamingNormalizer
{
    public string NormalizeContainerName(string containerName)
    {
        var normalized = containerName.ToLowerInvariant();
        if (normalized.Length > 63)
        {
            normalized = normalized[..63];
        }

        normalized = Regex.Replace(normalized, "[^a-z0-9-]", string.Empty);
        normalized = Regex.Replace(normalized, "-{2,}", "-");
        normalized = Regex.Replace(normalized, "^-", string.Empty);
        normalized = Regex.Replace(normalized, "-$", string.Empty);

        if (normalized.Length < 3)
        {
            normalized = normalized.PadRight(3, '0');
        }

        return normalized;
    }

    public string NormalizeBlobName(string blobName)
    {
        return blobName;
    }
}
