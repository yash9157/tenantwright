using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Shared.BlobStoring.Providers.FileSystem;

public sealed class FileSystemBlobNamingNormalizer : IBlobNamingNormalizer
{
    public string NormalizeContainerName(string containerName)
    {
        return Normalize(containerName, allowPathSeparator: false);
    }

    public string NormalizeBlobName(string blobName)
    {
        return Normalize(blobName, allowPathSeparator: true);
    }

    private static string Normalize(string value, bool allowPathSeparator)
    {
        using var _ = CultureScope.CreateInvariant();

        var normalized = value;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Allow directory separators for blob names only.
            var pattern = allowPathSeparator
                ? "[:\\*\\?\"<>\\|]"
                : "[:\\*\\?\"<>\\|/\\\\]";
            normalized = Regex.Replace(normalized, pattern, string.Empty);
        }

        return normalized;
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture;
        private readonly CultureInfo _originalUiCulture;

        private CultureScope()
        {
            _originalCulture = CultureInfo.CurrentCulture;
            _originalUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        public static CultureScope CreateInvariant()
        {
            return new CultureScope();
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }
}
