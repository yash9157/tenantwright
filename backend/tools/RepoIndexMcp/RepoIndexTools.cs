using System.ComponentModel;
using System.Text.Json;
using TenantWright.Tools.RepoIndex;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TenantWright.Tools.RepoIndexMcp;

[McpServerToolType]
public sealed class RepoIndexTools
{
    private readonly RepoIndexQueryService _queryService;

    public RepoIndexTools(RepoIndexQueryService queryService)
    {
        _queryService = queryService;
    }

    [McpServerTool(Name = "list_scopes", ReadOnly = true, UseStructuredContent = true, Title = "List Repo Scopes")]
    [Description("List all runtime scopes available in the repo index, including backend services, Auth, and ApiGateway.")]
    public Task<CallToolResult> ListScopes(
        [Description("Source mode: auto, index, or live. Auto prefers checked-in index JSON and falls back to live inspection when needed.")] string source = "auto")
    {
        return Task.FromResult(CreateStructuredResult(_queryService.ListScopes(source)));
    }

    [McpServerTool(Name = "get_scope_overview", ReadOnly = true, UseStructuredContent = true, Title = "Get Scope Overview")]
    [Description("Get the generated overview for one runtime scope, including high-signal entrypoints and key index paths.")]
    public Task<CallToolResult> GetScopeOverview(
        [Description("Scope name, for example Identity, Auth, or ApiGateway.")] string scope,
        [Description("Source mode: auto, index, or live.")] string source = "auto")
    {
        return Task.FromResult(CreateStructuredResult(_queryService.GetScopeOverview(scope, source)));
    }

    [McpServerTool(Name = "list_scope_projects", ReadOnly = true, UseStructuredContent = true, Title = "List Scope Projects")]
    [Description("List the indexed projects belonging to one runtime scope.")]
    public Task<CallToolResult> ListScopeProjects(
        [Description("Scope name, for example Identity, Auth, or ApiGateway.")] string scope,
        [Description("Source mode: auto, index, or live.")] string source = "auto")
    {
        return Task.FromResult(CreateStructuredResult(_queryService.ListScopeProjects(scope, source)));
    }

    [McpServerTool(Name = "list_scope_routes", ReadOnly = true, UseStructuredContent = true, Title = "List Scope Routes")]
    [Description("List indexed routes for one runtime scope, optionally filtered by HTTP method or route substring.")]
    public Task<CallToolResult> ListScopeRoutes(
        [Description("Scope name, for example Identity, Auth, or ApiGateway.")] string scope,
        [Description("Source mode: auto, index, or live.")] string source = "auto",
        [Description("Optional HTTP method filter such as GET or POST.")] string? method = null,
        [Description("Optional substring filter applied to the route template.")] string? routeContains = null)
    {
        return Task.FromResult(CreateStructuredResult(_queryService.ListScopeRoutes(scope, source, method, routeContains)));
    }

    [McpServerTool(Name = "list_scope_contracts", ReadOnly = true, UseStructuredContent = true, Title = "List Scope Contracts")]
    [Description("List indexed contracts for one runtime scope, optionally filtered by contract kind.")]
    public Task<CallToolResult> ListScopeContracts(
        [Description("Scope name, typically a backend service such as Identity or Appointments.")] string scope,
        [Description("Source mode: auto, index, or live.")] string source = "auto",
        [Description("Optional contract kind filter such as class, interface, or record.")] string? kind = null)
    {
        return Task.FromResult(CreateStructuredResult(_queryService.ListScopeContracts(scope, source, kind)));
    }

    [McpServerTool(Name = "get_scope_persistence", ReadOnly = true, UseStructuredContent = true, Title = "Get Scope Persistence")]
    [Description("Get the indexed persistence summary for one runtime scope, including DbContexts, entities, and migrations project paths.")]
    public Task<CallToolResult> GetScopePersistence(
        [Description("Scope name, for example Identity, Auth, or ApiGateway.")] string scope,
        [Description("Source mode: auto, index, or live.")] string source = "auto")
    {
        return Task.FromResult(CreateStructuredResult(_queryService.GetScopePersistence(scope, source)));
    }

    [McpServerTool(Name = "get_scope_dependencies", ReadOnly = true, UseStructuredContent = true, Title = "Get Scope Dependencies")]
    [Description("Get the indexed dependency summary for one runtime scope, including direct project references, cross-scope dependencies, shared modules, and orchestration references.")]
    public Task<CallToolResult> GetScopeDependencies(
        [Description("Scope name, for example Identity, Auth, or ApiGateway.")] string scope,
        [Description("Source mode: auto, index, or live.")] string source = "auto")
    {
        return Task.FromResult(CreateStructuredResult(_queryService.GetScopeDependencies(scope, source)));
    }

    [McpServerTool(Name = "find_routes", ReadOnly = true, UseStructuredContent = true, Title = "Find Routes")]
    [Description("Search indexed routes by route text, symbol, or file path, optionally inside one scope.")]
    public Task<CallToolResult> FindRoutes(
        [Description("Search text matched against route templates, symbols, and file paths.")] string query,
        [Description("Source mode: auto, index, or live.")] string source = "auto",
        [Description("Optional scope restriction, for example Identity or ApiGateway.")] string? scope = null)
    {
        return Task.FromResult(CreateStructuredResult(_queryService.FindRoutes(query, source, scope)));
    }

    [McpServerTool(Name = "find_contracts", ReadOnly = true, UseStructuredContent = true, Title = "Find Contracts")]
    [Description("Search indexed contracts by name, namespace, or file path, optionally inside one scope.")]
    public Task<CallToolResult> FindContracts(
        [Description("Search text matched against contract names, namespaces, and file paths.")] string query,
        [Description("Source mode: auto, index, or live.")] string source = "auto",
        [Description("Optional scope restriction, for example Identity.")] string? scope = null)
    {
        return Task.FromResult(CreateStructuredResult(_queryService.FindContracts(query, source, scope)));
    }

    [McpServerTool(Name = "list_shared_modules", ReadOnly = true, UseStructuredContent = true, Title = "List Shared Modules")]
    [Description("List shared modules from the checked-in index, optionally filtered to one runtime scope or service.")]
    public Task<CallToolResult> ListSharedModules(
        [Description("Source mode: index, auto, or live. Index is the intended default for this query.")] string source = "index",
        [Description("Optional scope or service name filter, for example Identity.")] string? usedByService = null)
    {
        return Task.FromResult(CreateStructuredResult(_queryService.ListSharedModules(source, usedByService)));
    }

    [McpServerTool(Name = "get_shared_module_usage", ReadOnly = true, UseStructuredContent = true, Title = "Get Shared Module Usage")]
    [Description("Return the indexed usage summary for one Shared.* module.")]
    public Task<CallToolResult> GetSharedModuleUsage(
        [Description("Shared module name, for example Shared.Caching or Shared.EventBus.")] string module,
        [Description("Source mode: index, auto, or live. Index is the intended default for this query.")] string source = "index")
    {
        return Task.FromResult(CreateStructuredResult(_queryService.GetSharedModuleUsage(module, source)));
    }

    private static CallToolResult CreateStructuredResult<T>(T result)
    {
        return new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(result, RepoIndexBuilder.JsonOptions)
        };
    }
}
