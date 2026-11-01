using Auth.Claims;
using Auth.Clients;
using Auth.Pages.Account;
using Auth.Pages.Account.Validation;
using Auth.Routing;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Passkeys;
using Identity.Application.Contracts.Users;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using OpenIddict.Abstractions;
using System.Text.Json;
using Tenant.Application.Contracts.Tenants;
using Xunit;

namespace Auth.Tests;

public sealed class LoginModelPlatformModeTests
{
    [Fact]
    public void OnGet_platform_mode_sets_platform_state_and_preserves_return_url_in_switch()
    {
        var model = CreateModel();
        model.Mode = "platform";

        var result = model.OnGet("/connect/authorize?client_id=tenantwright-angular");

        Assert.IsType<PageResult>(result);
        Assert.True(model.IsPlatformMode);
        Assert.Equal("platform", model.Mode);
        Assert.Null(model.Input.Tenant);
        Assert.StartsWith($"{AuthPageRoutes.SignIn}?mode=tenant", model.ModeSwitchUrl, StringComparison.Ordinal);
        Assert.Contains("ReturnUrl=", model.ModeSwitchUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnPost_platform_mode_posts_without_tenant_lookup_or_tenant_id()
    {
        var identityClient = new RecordingIdentityClient
        {
            StartResult = new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = "failed"
            }
        };
        var tenantClient = new RecordingTenantClient();
        var model = CreateModel(identityClient, tenantClient);
        model.Mode = "platform";
        model.ReturnUrl = "/connect/authorize?client_id=tenantwright-angular";
        model.Input = new LoginModel.LoginInputModel
        {
            Tenant = "default",
            UserNameOrEmailAddress = "hostadmin",
            Password = "123qwe"
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal(0, tenantClient.FindByNormalizedNameCallCount);
        Assert.Null(identityClient.LastStartTenantId);
        Assert.NotNull(identityClient.LastStartInput);
        Assert.Equal(InteractiveLoginMode.Platform, identityClient.LastStartInput.Mode);
        Assert.Null(identityClient.LastStartInput.TenantId);
        Assert.Equal("hostadmin", identityClient.LastStartInput.LoginInput);
        Assert.Null(model.Input.Tenant);
    }

    [Fact]
    public async Task Passkey_without_user_verification_redirects_to_code_only_mfa_challenge()
    {
        var replacementChallengeId = Guid.NewGuid();
        var identityClient = new RecordingIdentityClient
        {
            PasskeyAssertionResult = new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.RequiresMfa,
                ChallengeId = replacementChallengeId,
                ChallengeExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                PasskeyAvailable = false,
                AuthenticatorCodeAvailable = true,
                LoginResult = new LoginResultDto
                {
                    Succeeded = false
                }
            }
        };
        var model = CreatePasskeyModel(identityClient);
        model.TempData["Auth.InteractiveLogin.FlowState"] = JsonSerializer.Serialize(
            new InteractiveLoginFlowState
            {
                Mode = InteractiveLoginMode.Platform,
                LoginInput = "hostadmin",
                SessionId = Guid.NewGuid().ToString("D"),
                ChallengeId = Guid.NewGuid(),
                ChallengeExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                PasskeyAvailable = true,
                AuthenticatorCodeAvailable = true
            });
        model.Input = new PasskeyModel.PasskeyInputModel
        {
            CeremonyId = Guid.NewGuid(),
            ResponseJson = "{}"
        };

        var result = await model.OnPostCompleteAsync();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(AuthPageRoutes.Mfa, redirect.Url);
        var persistedJson = Assert.IsType<string>(
            model.TempData["Auth.InteractiveLogin.FlowState"]);
        var persistedState = JsonSerializer.Deserialize<InteractiveLoginFlowState>(persistedJson);
        Assert.NotNull(persistedState);
        Assert.Equal(replacementChallengeId, persistedState!.ChallengeId);
        Assert.False(persistedState.PasskeyAvailable);
        Assert.True(persistedState.AuthenticatorCodeAvailable);
        Assert.False(persistedState.RecoveryCodeAvailable);
        Assert.False(persistedState.PasskeyEnrollmentRequired);
    }

    [Fact]
    public async Task Passkey_without_user_verification_and_without_code_factor_stays_on_passkey_page()
    {
        var challengeId = Guid.NewGuid();
        const string message =
            "This passkey did not confirm device verification. Try a passkey from your phone, Windows Hello, or a security key.";
        var identityClient = new RecordingIdentityClient
        {
            PasskeyAssertionResult = new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = message,
                ChallengeId = challengeId,
                ChallengeExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                PasskeyAvailable = true,
                AuthenticatorCodeAvailable = false,
                RecoveryCodeAvailable = false
            }
        };
        var model = CreatePasskeyModel(identityClient);
        model.TempData["Auth.InteractiveLogin.FlowState"] = JsonSerializer.Serialize(
            new InteractiveLoginFlowState
            {
                Mode = InteractiveLoginMode.Platform,
                LoginInput = "hostadmin",
                SessionId = Guid.NewGuid().ToString("D"),
                ChallengeId = challengeId,
                ChallengeExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                PasskeyAvailable = true,
                AuthenticatorCodeAvailable = false,
                RecoveryCodeAvailable = false
            });
        model.Input = new PasskeyModel.PasskeyInputModel
        {
            CeremonyId = Guid.NewGuid(),
            ResponseJson = "{}"
        };

        var result = await model.OnPostCompleteAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal(message, model.ErrorMessage);
        Assert.True(model.HasChallengeContext);
        Assert.False(model.CodeBasedMfaAvailable);
        var persistedJson = Assert.IsType<string>(
            model.TempData["Auth.InteractiveLogin.FlowState"]);
        var persistedState = JsonSerializer.Deserialize<InteractiveLoginFlowState>(persistedJson);
        Assert.NotNull(persistedState);
        Assert.Equal(challengeId, persistedState!.ChallengeId);
        Assert.True(persistedState.PasskeyAvailable);
        Assert.False(persistedState.CodeBasedMfaAvailable);
    }

    private static LoginModel CreateModel(
        RecordingIdentityClient? identityClient = null,
        RecordingTenantClient? tenantClient = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddMvcCore();
        services.AddSingleton<IAuthenticationService, NoopAuthenticationService>();
        var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider
        };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("auth.example.test");

        var model = new LoginModel(
            identityClient ?? new RecordingIdentityClient(),
            tenantClient ?? new RecordingTenantClient(),
            new NoopAuthPageValidationService(),
            new TestWebHostEnvironment(),
            new NoopClaimsPrincipalHandler(),
            applicationManager: null!)
        {
            PageContext = new PageContext
            {
                HttpContext = httpContext
            },
            TempData = new TempDataDictionary(httpContext, new DictionaryTempDataProvider())
        };

        return model;
    }

    private static PasskeyModel CreatePasskeyModel(RecordingIdentityClient identityClient)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddMvcCore();
        services.AddSingleton<IAuthenticationService, NoopAuthenticationService>();
        var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider
        };

        return new PasskeyModel(
            identityClient,
            new RecordingTenantClient(),
            new NoopClaimsPrincipalHandler(),
            applicationManager: null!)
        {
            PageContext = new PageContext
            {
                HttpContext = httpContext
            },
            TempData = new TempDataDictionary(httpContext, new DictionaryTempDataProvider())
        };
    }

    private sealed class NoopAuthPageValidationService : IAuthPageValidationService
    {
        public Task ApplyAsync<TModel>(ModelStateDictionary modelState, TModel model, string prefix, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingTenantClient : ITenantClient
    {
        public int FindByNormalizedNameCallCount { get; private set; }

        public Task<TenantConfigurationDto?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<TenantConfigurationDto?>(null);

        public Task<TenantConfigurationDto?> FindByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default)
        {
            FindByNormalizedNameCallCount++;
            return Task.FromResult<TenantConfigurationDto?>(null);
        }

        public Task<PublicTenantRegistrationInitiateResult?> InitiatePublicRegistrationAsync(PublicTenantRegistrationInitiateInput input, string idempotencyKey, CancellationToken cancellationToken = default)
            => Task.FromResult<PublicTenantRegistrationInitiateResult?>(null);
        public Task<TenantProvisioningOperationDto?> GetPublicRegistrationAsync(Guid operationId, CancellationToken cancellationToken = default)
            => Task.FromResult<TenantProvisioningOperationDto?>(null);
    }

    private sealed class RecordingIdentityClient : IIdentityClient
    {
        public InteractiveLoginResultDto StartResult { get; set; } = new();
        public InteractiveLoginResultDto PasskeyAssertionResult { get; set; } = new();
        public InteractiveLoginStartInput? LastStartInput { get; private set; }
        public Guid? LastStartTenantId { get; private set; }

        public Task<InteractiveLoginResultDto> StartInteractiveLoginAsync(InteractiveLoginStartInput input, Guid? tenantId, CancellationToken cancellationToken = default)
        {
            LastStartInput = input;
            LastStartTenantId = tenantId;
            return Task.FromResult(StartResult);
        }

        public Task<LoginResultDto> CheckPasswordAsync(string loginInput, string password, Guid? tenantId = null, InteractiveLoginMode mode = InteractiveLoginMode.Tenant, string? sessionId = null, string? clientId = null, string? clientIpAddress = null, string? browserInfo = null, string? correlationId = null, string? device = null, string? location = null, string? twoFactorCode = null, string? twoFactorRecoveryCode = null, string? deviceFingerprint = null, bool rememberTrustedDevice = false, CancellationToken cancellationToken = default)
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
            => Task.FromResult(PasskeyAssertionResult);

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
            => throw new NotSupportedException();

        public Task<bool> ValidateSessionAsync(Guid userId, string sessionId, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<IdentitySessionDto>> GetSessionsAsync(Guid userId, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoopClaimsPrincipalHandler : IOpenIddictClaimsPrincipalHandler
    {
        public Task HandleAsync(System.Security.Claims.ClaimsPrincipal principal)
            => Task.CompletedTask;
    }

    private sealed class NoopAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, System.Security.Claims.ClaimsPrincipal principal, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context)
            => new Dictionary<string, object>(StringComparer.Ordinal);

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Auth.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
