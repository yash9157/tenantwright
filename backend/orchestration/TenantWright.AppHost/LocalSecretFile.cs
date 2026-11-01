using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TenantWright.AppHost;

/// <summary>How a generated local material file is protected once written.</summary>
internal enum LocalSecretFileAccess
{
    /// <summary>
    /// Secret material (broker passwords, private signing keys): read-only, and readable only by the current user
    /// (an owner-only, non-inherited ACL on Windows; mode <c>0400</c> elsewhere).
    /// </summary>
    OwnerOnly,

    /// <summary>Public material (verification keys): read-only for everyone (mode <c>0444</c> outside Windows).</summary>
    Public
}

/// <summary>
/// File primitives for local development material: create-once atomic writes, atomic replacement of derived files,
/// and the read-only/owner-only protection the messaging hosts require of key files.
/// </summary>
/// <remarks>
/// Every write goes to a sibling temporary file that is flushed and protected before it is renamed into place, so
/// a crash never leaves a partially written or briefly unprotected file at the final path.
/// </remarks>
internal static class LocalSecretFile
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead;
    private const UnixFileMode PublicMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates <paramref name="path"/> (and missing parents) as a directory only the current user can enter.</summary>
    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerDirectoryMode);
    }

    /// <summary>
    /// Atomically creates <paramref name="path"/> with <paramref name="contents"/> and <paramref name="access"/>
    /// protection. Returns <see langword="false"/> without touching the file when it already exists.
    /// </summary>
    public static bool TryCreate(string path, ReadOnlySpan<byte> contents, LocalSecretFileAccess access)
    {
        var temporaryPath = TemporaryPathFor(path);
        try
        {
            WriteTemporary(temporaryPath, contents);
            Protect(temporaryPath, access);
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            DeleteTemporary(temporaryPath);
        }
    }

    /// <summary>
    /// Atomically replaces <paramref name="path"/> with <paramref name="contents"/> unless it already holds exactly
    /// those bytes. Used for derived, non-secret files that are regenerated from the authoritative material.
    /// </summary>
    public static void WriteIfChanged(string path, ReadOnlySpan<byte> contents)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(contents))
        {
            return;
        }

        var temporaryPath = TemporaryPathFor(path);
        try
        {
            WriteTemporary(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteTemporary(temporaryPath);
        }
    }

    /// <summary>Re-applies <paramref name="access"/> to an existing file, repairing protection lost to manual edits.</summary>
    public static void Protect(string path, LocalSecretFileAccess access)
    {
        if (OperatingSystem.IsWindows())
        {
            if (access == LocalSecretFileAccess.OwnerOnly)
            {
                RestrictToCurrentUser(path);
            }

            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            return;
        }

        File.SetUnixFileMode(path, access == LocalSecretFileAccess.OwnerOnly ? OwnerOnlyMode : PublicMode);
    }

    private static void WriteTemporary(string temporaryPath, ReadOnlySpan<byte> contents)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerReadWrite;
        }

        using var stream = new FileStream(temporaryPath, options);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteTemporary(string temporaryPath)
    {
        if (!File.Exists(temporaryPath))
        {
            return;
        }

        // Windows refuses to delete a read-only file.
        File.SetAttributes(temporaryPath, FileAttributes.Normal);
        File.Delete(temporaryPath);
    }

    private static string TemporaryPathFor(string path)
        => Path.Combine(
            Path.GetDirectoryName(path) ?? throw new InvalidOperationException("A material file path must have a parent directory."),
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");

        // A protected DACL drops the inherited entries (Administrators, SYSTEM, other profile ACEs).
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        try
        {
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            throw new InvalidOperationException(
                $"Could not restrict '{path}' to the current user. Place LocalMessaging:MaterialPath on an NTFS volume the current user owns.",
                exception);
        }
    }
}
