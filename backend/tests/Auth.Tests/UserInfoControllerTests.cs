using System.Security.Claims;
using Auth.Clients;
using Auth.Controllers;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Passkeys;
using Identity.Application.Contracts.Users;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Shared.Security;
using Xunit;

namespace Auth.Tests;

public sealed class UserInfoControllerTests
{
    [Fact]
    public async Task GetUserInfoAsync_should_hydrate_profile_claims_from_identity_not_access_token_pii()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(OpenIddictConstants.Claims.Subject, userId.ToString("D")),
                new Claim(SharedClaimTypes.TenantId, tenantId.ToString("D"))
            ],
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme));
        principal.SetScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Roles);

        var identityClient = new RecordingIdentityClient
        {
            UserInfo = new UserInfoDto
            {
                Id = userId,
                TenantId = tenantId,
                UserName = "tenant.user",
                Email = "tenant.user@example.test",
                Roles = ["TenantAdmin"],
                IsActive = true,
                IsLockedOut = false
            }
        };

        var controller = new UserInfoController(identityClient)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(principal)
            }
        };

        var result = await controller.GetUserInfoAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var claims = Assert.IsAssignableFrom<IDictionary<string, object>>(ok.Value);
        Assert.Equal("tenant.user@example.test", claims[OpenIddictConstants.Claims.Email]);
        Assert.Equal("tenant.user", claims[OpenIddictConstants.Claims.Name]);
        Assert.Equal(["TenantAdmin"], Assert.IsAssignableFrom<List<string>>(claims[OpenIddictConstants.Claims.Role]));
        Assert.Equal(userId, identityClient.LastUserId);
        Assert.Equal(tenantId, identityClient.LastTenantId);
        Assert.True(identityClient.LastStrictAuthorization);
    }

    private static DefaultHttpContext CreateHttpContext(ClaimsPrincipal principal)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new StaticAuthenticationService(principal));

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private sealed class StaticAuthenticationService : IAuthenticationService
    {
        private readonly ClaimsPrincipal _principal;

        public StaticAuthenticationService(ClaimsPrincipal principal)
        {
            _principal = principal;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(_principal, scheme ?? string.Empty)));

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
    }

    private sealed class RecordingIdentityClient : IIdentityClient
    {
        public UserInfoDto? UserInfo { get; set; }
        public Guid LastUserId { get; private set; }
        public Guid? LastTenantId { get; private set; }
        public bool LastStrictAuthorization { get; private set; }

        public Task<UserInfoDto?> GetUserInfoByIdAsync(Guid userId, Guid? tenantId = null, bool strictAuthorization = false, bool bypassCache = false, CancellationToken cancellationToken = default)
        {
            LastUserId = userId;
            LastTenantId = tenantId;
            LastStrictAuthorization = strictAuthorization;
            return Task.FromResult(UserInfo);
        }

        public Task<LoginResultDto> CheckPasswordAsync(string loginInput, string password, Guid? tenantId = null, InteractiveLoginMode mode = InteractiveLoginMode.Tenant, string? sessionId = null, string? clientId = null, string? clientIpAddress = null, string? browserInfo = null, string? correlationId = null, string? device = null, string? location = null, string? twoFactorCode = null, string? twoFactorRecoveryCode = null, string? deviceFingerprint = null, bool rememberTrustedDevice = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<InteractiveLoginResultDto> StartInteractiveLoginAsync(InteractiveLoginStartInput input, Guid? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<InteractiveLoginResultDto> CompleteMfaAsync(CompleteMfaInput input, Guid? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<InteractiveLoginResultDto> CompleteTrustDeviceAsync(CompleteTrustDeviceInput input, Guid? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyEnrollmentAsync(InteractivePasskeyOptionsInput input, Guid? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<InteractiveLoginResultDto> CompleteInteractivePasskeyEnrollmentAsync(CompleteInteractivePasskeyInput input, Guid? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyAssertionAsync(InteractivePasskeyOptionsInput input, Guid? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<InteractiveLoginResultDto> CompleteInteractivePasskeyAssertionAsync(CompleteInteractivePasskeyInput input, Guid? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RequestPasswordResetAsync(PasswordResetRequestInput input, Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PasswordResetValidateResultDto> ValidatePasswordResetTokenAsync(PasswordResetValidateInput input, Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PasswordResetCompleteResultDto> CompletePasswordResetAsync(PasswordResetCompleteInput input, Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PasswordExpiredChangeResultDto> ChangeExpiredPasswordAsync(PasswordExpiredChangeInput input, Guid? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RequestEmailVerificationAsync(EmailVerificationRequestInput input, Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EmailVerificationResultDto> ConfirmEmailVerificationAsync(EmailVerificationConfirmInput input, Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ResendEmailVerificationAsync(EmailVerificationRequestInput input, Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PublicInvitationValidateResultDto> ValidatePublicInvitationAsync(PublicInvitationValidateInput input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PublicInvitationAcceptResultDto> AcceptPublicInvitationAsync(PublicInvitationAcceptInput input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UserInfoDto?> GetUserInfoAsync(string loginInput, Guid? tenantId = null, InteractiveLoginMode mode = InteractiveLoginMode.Tenant, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RevokeSessionAsync(Guid userId, string sessionId, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ValidateSessionAsync(Guid userId, string sessionId, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<IdentitySessionDto>> GetSessionsAsync(Guid userId, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
