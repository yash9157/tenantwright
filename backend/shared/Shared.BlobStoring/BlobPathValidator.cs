using System.IO;

namespace Shared.BlobStoring;

internal static class BlobPathValidator
{
    public static string NormalizeRelativePath(string blobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var normalized = blobName.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized))
        {
            throw new BlobConfigurationException("Blob name must be a relative path.");
        }

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            throw new BlobConfigurationException("Blob name resolved to an empty path.");
        }

        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..")
            {
                throw new BlobConfigurationException("Blob name cannot contain relative traversal segments.");
            }

            if (segment.Contains('\0', StringComparison.Ordinal))
            {
                throw new BlobConfigurationException("Blob name contains invalid null characters.");
            }
        }

        return string.Join('/', segments);
    }
}
