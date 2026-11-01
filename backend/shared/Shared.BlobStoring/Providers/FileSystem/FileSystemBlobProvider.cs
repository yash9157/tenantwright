using Microsoft.Extensions.Logging;
using System.IO;

namespace Shared.BlobStoring.Providers.FileSystem;

public sealed class FileSystemBlobProvider : IBlobProvider
{
    private readonly ILogger<FileSystemBlobProvider> _logger;

    public FileSystemBlobProvider(ILogger<FileSystemBlobProvider> logger)
    {
        _logger = logger;
    }

    public async Task SaveAsync(BlobProviderSaveArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = args.Configuration.GetFileSystemConfiguration();
        var path = GetFilePath(args, options);

        if (!args.OverrideExisting && File.Exists(path))
        {
            throw new BlobAlreadyExistsException(
                $"Blob '{args.BlobName}' already exists in container '{args.ContainerName}'.");
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new BlobConfigurationException("Invalid file path resolved for blob save operation.");
        }

        Directory.CreateDirectory(directory);
        var fileMode = args.OverrideExisting ? FileMode.Create : FileMode.CreateNew;

        await ExecuteWithRetryAsync(
            options,
            async () =>
            {
                var fileStreamOptions = new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = fileMode,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };

                await using var fileStream = new FileStream(path, fileStreamOptions);
                await args.BlobStream.CopyToAsync(fileStream, args.CancellationToken);
                await fileStream.FlushAsync(args.CancellationToken);
            },
            args.CancellationToken);
    }

    public Task<bool> DeleteAsync(BlobProviderDeleteArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = args.Configuration.GetFileSystemConfiguration();
        var path = GetFilePath(args, options);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<bool> ExistsAsync(BlobProviderExistsArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = args.Configuration.GetFileSystemConfiguration();
        var path = GetFilePath(args, options);
        return Task.FromResult(File.Exists(path));
    }

    public async Task<Stream?> GetOrNullAsync(BlobProviderGetArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = args.Configuration.GetFileSystemConfiguration();
        var path = GetFilePath(args, options);
        if (!File.Exists(path))
        {
            return null;
        }

        Stream? stream = null;
        await ExecuteWithRetryAsync(
            options,
            () =>
            {
                var fileStreamOptions = new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };
                stream = new FileStream(path, fileStreamOptions);
                return Task.CompletedTask;
            },
            args.CancellationToken);

        return stream;
    }

    public Task<Uri> GetReadUrlAsync(BlobProviderReadUrlArgs args)
    {
        throw new BlobProviderNotSupportedException(
            "FileSystem blob provider does not support signed read URLs.");
    }

    private async Task ExecuteWithRetryAsync(
        FileSystemBlobProviderOptions options,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        var maxAttempts = options.RetryCount + 1;
        var baseDelayMs = options.RetryBaseDelayMs;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                _logger.LogDebug(
                    ex,
                    "Retrying FileSystem blob operation after transient I/O failure. Attempt={Attempt}/{MaxAttempts}.",
                    attempt,
                    maxAttempts);

                await Task.Delay(TimeSpan.FromMilliseconds(baseDelayMs * attempt), cancellationToken);
            }
        }
    }

    private static string GetFilePath(BlobProviderArgs args, FileSystemBlobProviderOptions options)
    {
        var basePath = Path.GetFullPath(options.BasePath);
        var rootPath = options.AppendContainerNameToBasePath
            ? Path.GetFullPath(Path.Combine(basePath, args.ContainerName))
            : basePath;

        var candidatePath = rootPath;
        var segments = args.BlobName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            candidatePath = Path.Combine(candidatePath, segment);
        }

        rootPath = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(candidatePath);
        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        if (relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            throw new BlobConfigurationException("Resolved blob file path escaped the configured base path.");
        }

        return fullPath;
    }
}
