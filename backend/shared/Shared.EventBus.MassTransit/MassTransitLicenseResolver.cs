using MassTransit;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.MassTransit;

public sealed class MassTransitLicenseResolver
{
    internal const string LicenseEnvironmentVariable = "MT_LICENSE";
    internal const string LicensePathEnvironmentVariable = "MT_LICENSE_PATH";

    private readonly Func<string, string?> _readEnvironmentVariable;

    public MassTransitLicenseResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    internal MassTransitLicenseResolver(Func<string, string?> readEnvironmentVariable)
    {
        _readEnvironmentVariable = readEnvironmentVariable ??
            throw new ArgumentNullException(nameof(readEnvironmentVariable));
    }

    public void Apply(IBusFactoryConfigurator configurator)
    {
        ArgumentNullException.ThrowIfNull(configurator);

        var source = Resolve();
        if (source.Kind == MassTransitLicenseSourceKind.Inline)
        {
            configurator.SetLicense(source.Value);
            return;
        }

        configurator.SetLicenseLocation(source.Value);
    }

    internal MassTransitLicenseSource Resolve()
    {
        var license = _readEnvironmentVariable(LicenseEnvironmentVariable);
        var licensePath = _readEnvironmentVariable(LicensePathEnvironmentVariable);
        var hasLicense = license is not null;
        var hasLicensePath = licensePath is not null;

        if (hasLicense == hasLicensePath)
        {
            throw new InvalidOperationException(
                "A licensed MassTransit workload requires exactly one configured license source.");
        }

        if (hasLicense)
        {
            if (string.IsNullOrWhiteSpace(license))
            {
                throw new InvalidOperationException("The configured MassTransit license source is empty.");
            }

            return new MassTransitLicenseSource(MassTransitLicenseSourceKind.Inline, license);
        }

        if (string.IsNullOrWhiteSpace(licensePath))
        {
            throw new InvalidOperationException("The configured MassTransit license path is empty.");
        }

        return new MassTransitLicenseSource(
            MassTransitLicenseSourceKind.File,
            ValidateLicensePath(licensePath));
    }

    internal static string ValidateLicensePath(string configuredPath)
    {
        if (!Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException("The configured MassTransit license path must be absolute.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(configuredPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("The configured MassTransit license path is invalid.");
        }

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The configured MassTransit license path is not a regular file.");
            }

            if (!IsReadOnly(fullPath, attributes))
            {
                throw new InvalidOperationException("The configured MassTransit license file must be read-only.");
            }

            using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return fullPath;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or
                                           UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException("The configured MassTransit license file is inaccessible.");
        }
    }

    private static bool IsReadOnly(string path, FileAttributes attributes)
    {
        if (OperatingSystem.IsWindows())
        {
            return (attributes & FileAttributes.ReadOnly) != 0;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode writeModes = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        return (mode & writeModes) == 0;
    }
}

internal enum MassTransitLicenseSourceKind
{
    Inline = 1,
    File = 2
}

internal readonly record struct MassTransitLicenseSource(
    MassTransitLicenseSourceKind Kind,
    string Value);
