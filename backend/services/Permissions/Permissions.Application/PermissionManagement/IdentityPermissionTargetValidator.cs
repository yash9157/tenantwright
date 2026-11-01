using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using Identity.Domain.Shared.ApplicationUsers;

namespace Permissions.Application.PermissionManagement;

public interface IIdentityPermissionTargetValidator
{
    Task<IdentityRolePermissionTarget> GetRoleAsync(Guid roleId, Guid? tenantId, CancellationToken cancellationToken = default);
    Task<IdentityUserPermissionTarget> GetUserAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IdentityResolvedPermissionTarget>> ResolveAsync(
        IReadOnlyCollection<IdentityPermissionTargetReference> targets,
        CancellationToken cancellationToken = default);
}

public sealed record IdentityRolePermissionTarget(
    Guid RoleId,
    string Name,
    bool IsStatic,
    bool IsActive,
    long LifecycleVersion);

public sealed record IdentityUserPermissionTarget(
    Guid UserId,
    string UserName,
    string DisplayName,
    bool IsActive,
    bool IsLockedOut,
    IReadOnlyList<Guid> RoleIds);
public enum IdentityPermissionTargetKind { User = 1, Role = 2 }
public sealed record IdentityPermissionTargetReference(IdentityPermissionTargetKind Kind, Guid Id, Guid? TenantId);
public sealed record IdentityResolvedPermissionTarget(IdentityPermissionTargetKind Kind, Guid Id, Guid? TenantId, string DisplayName);

internal sealed class IdentityPermissionTargetValidator : IIdentityPermissionTargetValidator, ITransientDependency
{
    public const string HttpClientName = "Permissions.IdentityPermissionTargets";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PermissionIdentityInternalAccessTokenProvider _tokenProvider;
    private readonly IOptions<IdentityPermissionTargetValidationOptions> _options;
    private readonly ILogger<IdentityPermissionTargetValidator> _logger;

    public IdentityPermissionTargetValidator(
        IHttpClientFactory httpClientFactory,
        PermissionIdentityInternalAccessTokenProvider tokenProvider,
        IOptions<IdentityPermissionTargetValidationOptions> options,
        ILogger<IdentityPermissionTargetValidator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<IdentityRolePermissionTarget> GetRoleAsync(
        Guid roleId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("Role id is required.", nameof(roleId));
        }

        var tenantQuery = tenantId.HasValue ? $"?tenantId={tenantId.Value:D}" : string.Empty;
        var target = await SendAsync<RolePermissionTargetResponse>(
            $"api/identity/internal/permission-targets/roles/{roleId:D}{tenantQuery}",
            cancellationToken);
        return MapRole(target);
    }

    public async Task<IdentityUserPermissionTarget> GetUserAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        var tenantQuery = tenantId.HasValue ? $"?tenantId={tenantId.Value:D}" : string.Empty;
        var target = await SendAsync<UserPermissionTargetResponse>(
            $"api/identity/internal/permission-targets/users/{userId:D}{tenantQuery}",
            cancellationToken);
        return new IdentityUserPermissionTarget(
            userId,
            target.UserName,
            target.DisplayName,
            target.IsActive,
            target.IsLockedOut,
            target.RoleIds);
    }

    public async Task<IReadOnlyList<IdentityResolvedPermissionTarget>> ResolveAsync(
        IReadOnlyCollection<IdentityPermissionTargetReference> targets,
        CancellationToken cancellationToken = default)
    {
        if (targets is not { Count: > 0 and <= 200 })
        {
            return [];
        }

        var response = await SendAsync<List<ResolvedPermissionTargetResponse>>(
            "api/identity/internal/permission-targets/resolve",
            cancellationToken,
            new
            {
                targets = targets.Select(target => new { kind = (int)target.Kind, target.Id, target.TenantId }).ToList()
            });
        return response.Select(target => new IdentityResolvedPermissionTarget(
            (IdentityPermissionTargetKind)target.Kind,
            target.Id,
            target.TenantId,
            target.DisplayName)).ToList();
    }

    private async Task<TResponse> SendAsync<TResponse>(
        string requestPath,
        CancellationToken cancellationToken,
        object? body = null)
        where TResponse : class
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.BaseAddress) ||
            !Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Identity permission target validation BaseAddress is missing or invalid.");
        }

        var token = await _tokenProvider.GetAccessTokenAsync(options.Scope, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new UnauthorizedAccessException("Identity permission target validation token was unavailable.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, requestPath);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UnauthorizedAccessException("Permission assignment target was not found in Identity.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Identity permission target validation failed. StatusCode={StatusCode}, Path={Path}",
                    response.StatusCode,
                    requestPath);
                throw new UnauthorizedAccessException("Identity permission target validation failed.");
            }

            var payload = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken);
            if (payload == null)
            {
                throw new UnauthorizedAccessException("Identity permission target validation returned an empty response.");
            }

            return payload;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UnauthorizedAccessException("Identity permission target validation timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity permission target validation request failed. Path={Path}", requestPath);
            throw new UnauthorizedAccessException("Identity permission target validation failed.", ex);
        }
    }

    private static IdentityRolePermissionTarget MapRole(RolePermissionTargetResponse target)
        => new(
            target.Id,
            target.Name,
            target.IsStatic,
            target.LifecycleState == ApplicationRoleLifecycleState.Active,
            target.LifecycleVersion);

    private sealed class RolePermissionTargetResponse
    {
        public string Name { get; set; } = default!;
        public bool IsStatic { get; set; }
        public Guid Id { get; set; }
        public ApplicationRoleLifecycleState LifecycleState { get; set; }
        public long LifecycleVersion { get; set; }
    }

    private sealed class UserPermissionTargetResponse
    {
        public string Id { get; set; } = default!;
        public string UserName { get; set; } = default!;
        public string DisplayName { get; set; } = default!;
        public bool IsActive { get; set; }
        public bool IsLockedOut { get; set; }
        public List<Guid> RoleIds { get; set; } = [];
    }


    private sealed class ResolvedPermissionTargetResponse
    {
        public int Kind { get; set; }
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }
        public string DisplayName { get; set; } = default!;
    }
}
