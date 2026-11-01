using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Templates;
using Shared.Caching.Abstractions;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Templates;

internal interface IFileSystemTemplateSource
{
    IReadOnlyList<FileSystemTemplateDescriptor> DiscoverTemplates(bool includeLayout = false);
    Task<StoredNotificationTemplateContent?> GetAsync(string templateName, CancellationToken cancellationToken = default);
}

internal sealed class FileSystemTemplateDescriptor
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Category { get; init; } = "";
    public string RelativePath { get; init; } = "";
}

internal sealed class FileSystemTemplateStore : IFileSystemTemplateSource, ITransientDependency
{
    private static readonly HybridCacheEntryOptions TemplateCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(10)
    };

    private readonly IOptions<NotificationsTemplateOptions> _options;
    private readonly IHybridCache<string, string> _cache;

    public FileSystemTemplateStore(
        IOptions<NotificationsTemplateOptions> options,
        IHybridCache<string, string> cache)
    {
        _options = options;
        _cache = cache;
    }

    public IReadOnlyList<FileSystemTemplateDescriptor> DiscoverTemplates(bool includeLayout = false)
    {
        var options = _options.Value;
        var absoluteRoot = GetAbsoluteRoot(options);
        if (!Directory.Exists(absoluteRoot))
        {
            return [];
        }

        var normalizedExtension = options.Extension.StartsWith('.')
            ? options.Extension
            : "." + options.Extension;
        var normalizedLayout = options.LayoutTemplateName.Trim();

        return Directory
            .EnumerateFiles(absoluteRoot, "*" + normalizedExtension, SearchOption.AllDirectories)
            .Select(file => ToDescriptor(absoluteRoot, file, normalizedExtension))
            .Where(descriptor => includeLayout || !string.Equals(descriptor.Name, normalizedLayout, StringComparison.OrdinalIgnoreCase))
            .OrderBy(descriptor => descriptor.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<StoredNotificationTemplateContent?> GetAsync(string templateName, CancellationToken cancellationToken = default)
    {
        if (!TryResolveTemplatePath(templateName, out var absolutePath, out var relativePath))
        {
            return null;
        }

        var versionTicks = File.GetLastWriteTimeUtc(absolutePath).Ticks;
        var cacheKey = $"notifications:template:file:{absolutePath}:{versionTicks}";

        return await _cache.GetOrCreateAsync(
            cacheKey,
            () => File.ReadAllTextAsync(absolutePath, cancellationToken),
            optionsFactory: () => TemplateCacheOptions,
            considerUow: false,
            token: cancellationToken)
            .ContinueWith(
                task => task.Result is null
                    ? null
                    : new StoredNotificationTemplateContent
                    {
                        TemplateName = templateName.Trim(),
                        BodyTemplate = task.Result,
                        RelativePath = relativePath,
                        SourceType = "FileSystem"
                    },
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private bool TryResolveTemplatePath(string templateName, out string absolutePath, out string relativePath)
    {
        absolutePath = string.Empty;
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(templateName))
        {
            return false;
        }

        var options = _options.Value;
        var normalizedExtension = options.Extension.StartsWith('.')
            ? options.Extension
            : "." + options.Extension;
        var normalized = templateName.Trim().Replace('.', '/');
        relativePath = string.Equals(templateName.Trim(), options.LayoutTemplateName, StringComparison.OrdinalIgnoreCase)
            ? $"{options.LayoutTemplateName}{normalizedExtension}"
            : $"{normalized}{normalizedExtension}";

        absolutePath = Path.Combine(GetAbsoluteRoot(options), relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(absolutePath);
    }

    private static string GetAbsoluteRoot(NotificationsTemplateOptions options)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.RootPath));
    }

    private static FileSystemTemplateDescriptor ToDescriptor(string absoluteRoot, string filePath, string extension)
    {
        var relativePath = Path.GetRelativePath(absoluteRoot, filePath).Replace('\\', '/');
        var withoutExtension = relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? relativePath[..^extension.Length]
            : relativePath;
        var segments = withoutExtension.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var category = segments.Length > 1 ? string.Join(" / ", segments[..^1]) : "General";
        var name = string.Join('.', segments);

        return new FileSystemTemplateDescriptor
        {
            Category = category,
            DisplayName = segments[^1].Replace('-', ' '),
            Name = name,
            RelativePath = relativePath
        };
    }
}
