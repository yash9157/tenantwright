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
    public async Task RequestEmailVerificationAsync(
        EmailVerificationRequestInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/email-verification/request")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var _ = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for email verification request.");
        }
    }

    public async Task<EmailVerificationResultDto> ConfirmEmailVerificationAsync(
        EmailVerificationConfirmInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/email-verification/confirm")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<EmailVerificationResultDto>(cancellationToken: cancellationToken)
                       ?? new EmailVerificationResultDto();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for email verification confirmation.");
        }

        return new EmailVerificationResultDto { Succeeded = false, Message = "Email verification failed." };
    }

    public async Task ResendEmailVerificationAsync(
        EmailVerificationRequestInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/email-verification/resend")
            {
                Content = JsonContent.Create(input)
            };

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var _ = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for email verification resend.");
        }
    }

    public async Task<PublicInvitationValidateResultDto> ValidatePublicInvitationAsync(
        PublicInvitationValidateInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/public-invitations/validate")
            {
                Content = JsonContent.Create(input)
            };

            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PublicInvitationValidateResultDto>(cancellationToken: cancellationToken)
                       ?? new PublicInvitationValidateResultDto
                       {
                           Succeeded = false,
                           Message = "Invitation validation failed."
                       };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for invitation validation.");
        }

        return new PublicInvitationValidateResultDto
        {
            Succeeded = false,
            Message = "Invitation validation failed."
        };
    }

    public async Task<PublicInvitationAcceptResultDto> AcceptPublicInvitationAsync(
        PublicInvitationAcceptInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/account/public-invitations/accept")
            {
                Content = JsonContent.Create(input)
            };

            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PublicInvitationAcceptResultDto>(cancellationToken: cancellationToken)
                       ?? new PublicInvitationAcceptResultDto
                       {
                           Succeeded = false,
                           Message = "Invitation acceptance failed."
                       };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for invitation acceptance.");
        }

        return new PublicInvitationAcceptResultDto
        {
            Succeeded = false,
            Message = "Invitation acceptance failed."
        };
    }

}
