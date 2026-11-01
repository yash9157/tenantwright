using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tenant.Application.Contracts.Tenants;

namespace Auth.Clients;

public interface ITenantClient
{
    Task<TenantConfigurationDto?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TenantConfigurationDto?> FindByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);
    Task<PublicTenantRegistrationInitiateResult?> InitiatePublicRegistrationAsync(PublicTenantRegistrationInitiateInput input, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<TenantProvisioningOperationDto?> GetPublicRegistrationAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public class TenantClient : ITenantClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TenantClient> _logger;
    private readonly IInternalAccessTokenProvider _tokenProvider;

    public TenantClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<TenantClient> logger,
        IInternalAccessTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        var baseAddress = configuration["Services:Tenant:Url"];
        if (!string.IsNullOrWhiteSpace(baseAddress))
        {
            _httpClient.BaseAddress = new Uri(baseAddress);
        }
        _logger = logger;
        _tokenProvider = tokenProvider;
    }

    public async Task<TenantConfigurationDto?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        if (!EnsureConfigured())
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/tenant/internal/tenants/{id:D}");
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tenant service internal lookup by id failed. Status: {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TenantConfigurationDto?>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Tenant service for internal tenant lookup by id.");
            return null;
        }
    }

    public async Task<TenantConfigurationDto?> FindByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        if (!EnsureConfigured())
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/tenant/internal/tenants/by-name/{Uri.EscapeDataString(normalizedName)}");
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tenant service internal lookup failed. Status: {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TenantConfigurationDto?>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Tenant service for internal tenant lookup.");
            return null;
        }
    }

    public async Task<PublicTenantRegistrationInitiateResult?> InitiatePublicRegistrationAsync(
        PublicTenantRegistrationInitiateInput input,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (input == null || input.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey) || !EnsureConfigured()) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/tenant/internal/tenants/public-registrations")
            {
                Content = JsonContent.Create(input)
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tenant public-registration initiation failed. Status: {StatusCode}", response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<PublicTenantRegistrationInitiateResult>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Tenant service for public registration initiation.");
            return null;
        }
    }

    public async Task<TenantProvisioningOperationDto?> GetPublicRegistrationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || !EnsureConfigured()) return null;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/tenant/internal/tenants/public-registrations/{operationId:D}");
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tenant public-registration status lookup failed. Status: {StatusCode}",
                    response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TenantProvisioningOperationDto?>(
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Tenant service for public-registration status.");
            return null;
        }
    }

    private async Task AddInternalAuthorizationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Tenant resolution is the authority lookup used to validate a requested tenant.
        // It must use the host-scoped internal token; acquiring a tenant-bound token from
        // Auth would recurse through this same lookup endpoint. Downstream owner adapters
        // may request tenant-bound tokens after resolution succeeds.
        var token = await _tokenProvider.GetAccessTokenAsync("tenant-internal", cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private bool EnsureConfigured()
    {
        if (_httpClient.BaseAddress != null)
        {
            return true;
        }

        _logger.LogError("Services:Tenant:Url is required.");
        return false;
    }

    private static async Task<string?> TryReadErrorDescriptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (json.ValueKind == JsonValueKind.Object &&
                json.TryGetProperty("error_description", out var errorDescription))
            {
                return errorDescription.GetString();
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }
}
