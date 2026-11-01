using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace TenantWright.AppHost.Tests;

/// <summary>A disposable directory outside the repository for generated material.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tenantwright-apphost-tests-{Guid.NewGuid():N}");
    }

    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        // Generated key and password files are read-only; Windows refuses to delete those.
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Path, recursive: true);
    }
}

internal sealed class DevelopmentHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "TenantWright.AppHost.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal static class RepositoryFiles
{
    public static string Root { get; } = FindRoot();

    public static string ReadAppHost(string fileName) => File.ReadAllText(AppHostPath(fileName));

    public static string AppHostPath(string fileName) => Path.Combine(Root, "orchestration", "TenantWright.AppHost", fileName);

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "TenantWright.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the TenantWright repository root.");
    }
}
