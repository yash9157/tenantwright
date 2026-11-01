using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Auth.Caching;
using Auth.Clients.Caching;
using Identity.Application.Contracts.Account;
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
    public async Task RequestPasswordResetAsync(
        PasswordResetRequestInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/password-reset/request")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var _ = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for password reset request.");
        }
    }

    public async Task<PasswordResetValidateResultDto> ValidatePasswordResetTokenAsync(
        PasswordResetValidateInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/password-reset/validate")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PasswordResetValidateResultDto>(cancellationToken: cancellationToken)
                       ?? new PasswordResetValidateResultDto();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for password reset token validation.");
        }

        return new PasswordResetValidateResultDto();
    }

    public async Task<PasswordResetCompleteResultDto> CompletePasswordResetAsync(
        PasswordResetCompleteInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/password-reset/complete")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PasswordResetCompleteResultDto>(cancellationToken: cancellationToken)
                       ?? new PasswordResetCompleteResultDto();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for password reset completion.");
        }

        return new PasswordResetCompleteResultDto();
    }

    public async Task<PasswordExpiredChangeResultDto> ChangeExpiredPasswordAsync(
        PasswordExpiredChangeInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/password-expired/change")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PasswordExpiredChangeResultDto>(cancellationToken: cancellationToken)
                       ?? new PasswordExpiredChangeResultDto();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for expired-password change.");
        }

        return new PasswordExpiredChangeResultDto();
    }

}
