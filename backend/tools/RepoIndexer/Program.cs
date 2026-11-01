using TenantWright.Tools.RepoIndex;

if (CliArguments.IsHelpRequest(args))
{
    Console.Error.WriteLine(CliArguments.GetUsage("RepoIndexer"));
    return;
}

var arguments = CliArguments.Parse(args);
var repoRoot = RepositoryPaths.ResolveRepoRoot(arguments.RepoRoot);
var solutionPath = RepositoryPaths.ResolveSolutionPath(repoRoot, arguments.SolutionPath);
var outputPath = RepositoryPaths.ResolveOutputPath(repoRoot, arguments.OutputPath);

var indexer = new RepoIndexBuilder(repoRoot, solutionPath);
await indexer.WriteAsync(outputPath);
