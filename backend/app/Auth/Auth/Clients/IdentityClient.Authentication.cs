using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Auth.Caching;
using Auth.Clients.Caching;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Passkeys;
using Identity.Domain.Shared.Account;
using Identity.Application.Contracts.Users;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;

namespace Auth.Clients;

public partial class IdentityClient
{
    public async Task<LoginResultDto> CheckPasswordAsync(
        string loginInput,
        string password,
        Guid? tenantId = null,
        InteractiveLoginMode mode = InteractiveLoginMode.Tenant,
        string? sessionId = null,
        string? clientId = null,
        string? clientIpAddress = null,
        string? browserInfo = null,
        string? correlationId = null,
        string? device = null,
        string? location = null,
        string? twoFactorCode = null,
        string? twoFactorRecoveryCode = null,
        string? deviceFingerprint = null,
        bool rememberTrustedDevice = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/check-password")
            {
                Content = JsonContent.Create(new
                {
                    LoginInput = loginInput,
                    Password = password,
                    TenantId = tenantId,
                    Mode = mode,
                    SessionId = sessionId,
                    ClientId = clientId,
                    ClientIpAddress = clientIpAddress,
                    BrowserInfo = browserInfo,
                    CorrelationId = correlationId,
                    Device = device,
                    Location = location,
                    TwoFactorCode = twoFactorCode,
                    TwoFactorRecoveryCode = twoFactorRecoveryCode,
                    DeviceFingerprint = deviceFingerprint,
                    RememberTrustedDevice = rememberTrustedDevice
                })
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<LoginResultDto>(cancellationToken: cancellationToken)
                       ?? new LoginResultDto();
            }

            _logger.LogWarning("Identity service check-password failed. Status: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for check-password.");
        }

        return new LoginResultDto();
    }

    public async Task<InteractiveLoginResultDto> StartInteractiveLoginAsync(
        InteractiveLoginStartInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/interactive/start")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<InteractiveLoginResultDto>(cancellationToken: cancellationToken)
                       ?? new InteractiveLoginResultDto();
            }

            _logger.LogWarning("Identity interactive login start failed. Status: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for interactive login start.");
        }

        return new InteractiveLoginResultDto();
    }

    public async Task<InteractiveLoginResultDto> CompleteMfaAsync(
        CompleteMfaInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/interactive/mfa/complete")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<InteractiveLoginResultDto>(cancellationToken: cancellationToken)
                       ?? new InteractiveLoginResultDto();
            }

            _logger.LogWarning("Identity interactive MFA completion failed. Status: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for interactive MFA completion.");
        }

        return new InteractiveLoginResultDto();
    }

    public async Task<InteractiveLoginResultDto> CompleteTrustDeviceAsync(
        CompleteTrustDeviceInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        return await SendInteractiveAsync<CompleteTrustDeviceInput, InteractiveLoginResultDto>(
            "api/identity/account/interactive/trust-device/complete",
            input,
            tenantId,
            "trust-device completion",
            cancellationToken);
    }

    public Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyEnrollmentAsync(
        InteractivePasskeyOptionsInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
        => SendInteractiveAsync<InteractivePasskeyOptionsInput, PasskeyCeremonyOptionsDto>(
            "api/identity/account/interactive/passkey/enrollment/options", input, tenantId, "passkey enrollment options", cancellationToken);

    public Task<InteractiveLoginResultDto> CompleteInteractivePasskeyEnrollmentAsync(
        CompleteInteractivePasskeyInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
        => SendInteractiveAsync<CompleteInteractivePasskeyInput, InteractiveLoginResultDto>(
            "api/identity/account/interactive/passkey/enrollment/complete", input, tenantId, "passkey enrollment completion", cancellationToken);

    public Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyAssertionAsync(
        InteractivePasskeyOptionsInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
        => SendInteractiveAsync<InteractivePasskeyOptionsInput, PasskeyCeremonyOptionsDto>(
            "api/identity/account/interactive/passkey/assertion/options", input, tenantId, "passkey assertion options", cancellationToken);

    public Task<InteractiveLoginResultDto> CompleteInteractivePasskeyAssertionAsync(
        CompleteInteractivePasskeyInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
        => SendInteractiveAsync<CompleteInteractivePasskeyInput, InteractiveLoginResultDto>(
            "api/identity/account/interactive/passkey/assertion/complete", input, tenantId, "passkey assertion completion", cancellationToken);

    private async Task<TResponse> SendInteractiveAsync<TInput, TResponse>(
        string path,
        TInput input,
        Guid? tenantId,
        string operation,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(input)
        };

        AddTenantHeader(request, tenantId);
        await AddInternalAuthorizationAsync(request, cancellationToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException($"Identity {operation} returned an empty response.");
        }

        var description = await TryReadErrorDescriptionAsync(response, cancellationToken);
        _logger.LogWarning("Identity {Operation} failed. Status: {StatusCode}", operation, response.StatusCode);
        throw new InvalidOperationException(description ?? $"Identity {operation} failed.");
    }

}
