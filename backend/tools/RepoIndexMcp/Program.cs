using TenantWright.Tools.RepoIndex;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (CliArguments.IsHelpRequest(args))
{
    Console.Error.WriteLine(CliArguments.GetUsage("RepoIndexMcp"));
    return;
}

var arguments = CliArguments.Parse(args);
var repoRoot = RepositoryPaths.ResolveRepoRoot(arguments.RepoRoot);
var solutionPath = RepositoryPaths.ResolveSolutionPath(repoRoot, arguments.SolutionPath);
var indexPath = RepositoryPaths.ResolveIndexPath(repoRoot, arguments.OutputPath);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    // MCP stdio transport reserves stdout for protocol frames only.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.Configure<ConsoleLifetimeOptions>(options =>
{
    options.SuppressStatusMessages = true;
});

builder.Services.AddMcpServer()
    .WithToolsFromAssembly(typeof(Program).Assembly)
    .WithStdioServerTransport();

builder.Services.AddSingleton(new RepoIndexMcpOptions(repoRoot, solutionPath, indexPath));
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<RepoIndexMcpOptions>();
    return new RepoIndexQueryService(options.RepoRoot, options.SolutionPath, options.IndexPath);
});

using var app = builder.Build();
await app.RunAsync();

internal sealed record RepoIndexMcpOptions(string RepoRoot, string SolutionPath, string IndexPath);
