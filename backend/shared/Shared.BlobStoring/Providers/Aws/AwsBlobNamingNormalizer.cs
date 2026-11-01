using System.Globalization;
using System.Text.RegularExpressions;

namespace Shared.BlobStoring.Providers.Aws;

public sealed class AwsBlobNamingNormalizer : IBlobNamingNormalizer
{
    public string NormalizeContainerName(string containerName)
    {
        var normalized = containerName.ToLowerInvariant();
        if (normalized.Length > 63)
        {
            normalized = normalized[..63];
        }

        normalized = Regex.Replace(normalized, "[^a-z0-9-.]", string.Empty);
        normalized = Regex.Replace(normalized, "\\.{2,}", ".");
        normalized = Regex.Replace(normalized, "-\\.", string.Empty);
        normalized = Regex.Replace(normalized, "\\.-", string.Empty);
        normalized = Regex.Replace(normalized, "^-", string.Empty);
        normalized = Regex.Replace(normalized, "-$", string.Empty);
        normalized = Regex.Replace(normalized, "^\\.", string.Empty);
        normalized = Regex.Replace(normalized, "\\.$", string.Empty);
        normalized = Regex.Replace(normalized, "^(?:(?:^|\\.)(?:2(?:5[0-5]|[0-4]\\d)|1?\\d?\\d)){4}$", string.Empty);

        if (normalized.Length < 3)
        {
            normalized = normalized.PadRight(3, '0');
        }

        return normalized;
    }

    public string NormalizeBlobName(string blobName)
    {
        _ = CultureInfo.InvariantCulture;
        return blobName;
    }
}
