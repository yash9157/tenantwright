using System.Security.Claims;
using Auth.Clients;
using Auth.Pages.Account;
using Auth.Routing;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Passkeys;
using Identity.Application.Contracts.Users;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using Shared.Security;
using Xunit;

namespace Auth.Tests;

public sealed class LogoutModelTests
{
    [Fact]
    public async Task OnPostAsync_should_revoke_tenant_session_before_signing_out()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");
        var identityClient = new RecordingIdentityClient();
        var httpContext = CreateHttpContext(userId, tenantId, sessionId);
        var model = CreateModel(identityClient, httpContext);

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(AuthPageRoutes.SignIn, redirect.Url);
        Assert.Equal((userId, sessionId, tenantId), identityClient.LastRevocation);
        Assert.Equal(1, httpContext.RequestServices.GetRequiredService<RecordingAuthenticationService>().SignOutCount);
    }

    [Fact]
    public async Task OnPostAsync_should_still_sign_out_when_remote_revocation_fails()
    {
        var httpContext = CreateHttpContext(
            Guid.NewGuid(),
            tenantId: null,
            sessionId: Guid.NewGuid().ToString("D"));
        var identityClient = new RecordingIdentityClient
        {
            RevocationException = new HttpRequestException("Identity is unavailable.")
        };
        var model = CreateModel(identityClient, httpContext);

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(AuthPageRoutes.SignIn, redirect.Url);
        Assert.Equal(1, httpContext.RequestServices.GetRequiredService<RecordingAuthenticationService>().SignOutCount);
    }

    private static LogoutModel CreateModel(
        IIdentityClient identityClient,
        DefaultHttpContext httpContext)
    {
        return new LogoutModel(identityClient, NullLogger<LogoutModel>.Instance)
        {
            PageContext = new PageContext
            {
                HttpContext = httpContext
            }
        };
    }

    private static DefaultHttpContext CreateHttpContext(
        Guid userId,
        Guid? tenantId,
        string sessionId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingAuthenticationService>();
        services.AddSingleton<IAuthenticationService>(provider =>
            provider.GetRequiredService<RecordingAuthenticationService>());

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                CreateClaims(userId, tenantId, sessionId),
                "test"))
        };
    }

    private static IEnumerable<Claim> CreateClaims(
        Guid userId,
        Guid? tenantId,
        string sessionId)
    {
        yield return new Claim(OpenIddictConstants.Claims.Subject, userId.ToString("D"));
        yield return new Claim(SharedClaimTypes.SessionId, sessionId);
        if (tenantId.HasValue)
        {
            yield return new Claim(SharedClaimTypes.TenantId, tenantId.Value.ToString("D"));
        }
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public int SignOutCount { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignOutCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingIdentityClient : IIdentityClient
    {
        public (Guid UserId, string SessionId, Guid? TenantId)? LastRevocation { get; private set; }
        public Exception? RevocationException { get; init; }

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

        public Task<UserInfoDto?> GetUserInfoByIdAsync(Guid userId, Guid? tenantId = null, bool strictAuthorization = false, bool bypassCache = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RevokeSessionAsync(Guid userId, string sessionId, Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            LastRevocation = (userId, sessionId, tenantId);
            if (RevocationException is not null)
            {
                return Task.FromException(RevocationException);
            }

            return Task.CompletedTask;
        }

        public Task<bool> ValidateSessionAsync(Guid userId, string sessionId, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<IdentitySessionDto>> GetSessionsAsync(Guid userId, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
