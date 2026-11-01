using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Auth.Claims;
using Auth.Clients;
using Auth.Routing;
using Auth.Pages.Account.Validation;
using Identity.Application.Contracts.Account;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-login")]
public class LoginModel : AuthInteractivePageModelBase
{
    private const string GenericLoginFailureMessage = "We couldn't sign you in with those details.";
    private const string DeviceFingerprintCookieName = "tenantwright.device.fp";
    private const int DeviceFingerprintCookieDays = 365;
    private const string TenantModeValue = "tenant";
    private const string PlatformModeValue = "platform";

    private readonly IIdentityClient _identityClient;
    private readonly ITenantClient _tenantClient;
    private readonly IAuthPageValidationService _pageValidationService;
    private readonly bool _isDevelopment;

    public LoginModel(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IAuthPageValidationService pageValidationService,
        IWebHostEnvironment environment,
        IOpenIddictClaimsPrincipalHandler claimsPrincipalHandler,
        IOpenIddictApplicationManager applicationManager)
        : base(identityClient, tenantClient, claimsPrincipalHandler, applicationManager)
    {
        _identityClient = identityClient;
        _tenantClient = tenantClient;
        _pageValidationService = pageValidationService;
        _isDevelopment = environment.IsDevelopment();
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Mode { get; set; }

    [BindProperty]
    public LoginInputModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public InteractiveLoginMode LoginMode { get; private set; } = InteractiveLoginMode.Tenant;
    public bool IsPlatformMode => LoginMode == InteractiveLoginMode.Platform;
    public string ModeSwitchUrl => BuildModeSwitchUrl();

    [BindProperty(SupportsGet = true)]
    public string? Tenant { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? LoginInput { get; set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        LoginMode = ParseMode(Mode);
        Mode = ToModeValue(LoginMode);
        ReturnUrl = returnUrl;
        if (string.IsNullOrWhiteSpace(ReturnUrl))
        {
            ReturnUrl = Request.Query["ReturnUrl"];
        }

        var passwordChangedState = PasswordChangedSuccessStateStore.Read(TempData, keep: false);
        if (passwordChangedState != null)
        {
            if (string.IsNullOrWhiteSpace(ReturnUrl))
            {
                ReturnUrl = passwordChangedState.ReturnUrl;
            }

            LoginMode = passwordChangedState.Mode;
            Mode = ToModeValue(LoginMode);

            if (string.IsNullOrWhiteSpace(Tenant))
            {
                Tenant = passwordChangedState.Tenant;
            }

            if (string.IsNullOrWhiteSpace(LoginInput))
            {
                LoginInput = passwordChangedState.LoginInput;
            }
        }

        if (!IsPlatformMode && !string.IsNullOrWhiteSpace(Tenant))
        {
            Input.Tenant = AuthInputConstraints.NormalizeTenant(Tenant);
        }

        if (!string.IsNullOrWhiteSpace(LoginInput))
        {
            Input.UserNameOrEmailAddress = AuthInputConstraints.NormalizeLoginInput(LoginInput);
        }

        Input.DeviceFingerprint = AuthInputConstraints.NormalizeDeviceFingerprint(GetOrCreateDeviceFingerprint());
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        LoginMode = ParseMode(Mode);
        Mode = ToModeValue(LoginMode);
        Input.Tenant = IsPlatformMode
            ? null
            : AuthInputConstraints.NormalizeTenant(Input.Tenant);
        Input.UserNameOrEmailAddress = AuthInputConstraints.NormalizeLoginInput(Input.UserNameOrEmailAddress);
        Input.Password = AuthInputConstraints.NormalizePassword(Input.Password);
        Input.DeviceFingerprint = AuthInputConstraints.NormalizeDeviceFingerprint(GetOrCreateDeviceFingerprint());

        ModelState.Clear();
        if (!await this.TryValidateInputAsync(_pageValidationService, Input, nameof(Input), HttpContext.RequestAborted))
        {
            return Page();
        }

        if (!IsPlatformMode && string.IsNullOrWhiteSpace(Input.Tenant))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Tenant)}", "Organization ID is required.");
            return Page();
        }

        var returnUrl = ReturnUrl;
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            returnUrl = Request.Query["ReturnUrl"];
        }

        Guid? tenantId = null;
        if (!IsPlatformMode)
        {
            var tenantInput = Input.Tenant;
            if (string.IsNullOrWhiteSpace(tenantInput))
            {
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Tenant)}", "Organization ID is required.");
                return Page();
            }

            var tenantNormalizedName = tenantInput.Trim().ToUpperInvariant();
            var tenant = await _tenantClient.FindByNormalizedNameAsync(tenantNormalizedName, HttpContext.RequestAborted);
            if (tenant == null)
            {
                ErrorMessage = GenericLoginFailureMessage;
                return Page();
            }

            if (!tenant.IsActive)
            {
                ErrorMessage = GenericLoginFailureMessage;
                return Page();
            }

            tenantId = tenant.Id;
        }

        var flowState = new InteractiveLoginFlowState
        {
            Mode = LoginMode,
            TenantId = tenantId,
            Tenant = Input.Tenant ?? string.Empty,
            LoginInput = Input.UserNameOrEmailAddress,
            SessionId = Guid.NewGuid().ToString("N"),
            ReturnUrl = returnUrl,
            RememberMe = Input.RememberMe,
            DeviceFingerprint = Input.DeviceFingerprint
        };

        var startResult = await _identityClient.StartInteractiveLoginAsync(
            new InteractiveLoginStartInput
            {
                Mode = LoginMode,
                TenantId = tenantId,
                LoginInput = Input.UserNameOrEmailAddress,
                Password = Input.Password,
                SessionId = flowState.SessionId,
                ClientId = GetClientIdFromReturnUrl(returnUrl),
                ClientIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                BrowserInfo = Request.Headers.UserAgent.ToString(),
                CorrelationId = HttpContext.TraceIdentifier,
                Device = Input.DeviceFingerprint,
                DeviceFingerprint = Input.DeviceFingerprint,
                PromptForTrustDevice = true
            },
            tenantId,
            HttpContext.RequestAborted);

        switch (startResult.Step)
        {
            case InteractiveLoginStep.Succeeded:
                {
                    var finalize = await FinalizeSignInAsync(flowState, HttpContext.RequestAborted);
                    if (finalize.Success && finalize.Result != null)
                    {
                        return finalize.Result;
                    }

                    ErrorMessage = finalize.Error ?? "Could not complete sign in.";
                    return Page();
                }
            case InteractiveLoginStep.RequiresMfa:
                if (!startResult.ChallengeId.HasValue)
                {
                    ErrorMessage = "Something went wrong. Please try again.";
                    return Page();
                }
                flowState.ChallengeId = startResult.ChallengeId;
                flowState.ChallengeExpiresAtUtc = startResult.ChallengeExpiresAtUtc;
                flowState.PasskeyAvailable = startResult.PasskeyAvailable;
                flowState.AuthenticatorCodeAvailable = startResult.AuthenticatorCodeAvailable;
                flowState.RecoveryCodeAvailable = startResult.RecoveryCodeAvailable;
                if (!flowState.PasskeyAvailable && !flowState.CodeBasedMfaAvailable)
                {
                    ErrorMessage = "No configured verification method is available for this account.";
                    return Page();
                }
                InteractiveLoginFlowStateStore.Save(TempData, flowState);
                return Redirect(
                    startResult.PasskeyAvailable
                        ? AuthPageRoutes.Passkey
                        : startResult.AuthenticatorCodeAvailable
                            ? AuthPageRoutes.Mfa
                            : AuthPageRoutes.RecoveryCode);
            case InteractiveLoginStep.RequiresMfaEnrollment:
                if (!startResult.ChallengeId.HasValue)
                {
                    ErrorMessage = "Something went wrong. Please try again.";
                    return Page();
                }
                flowState.ChallengeId = startResult.ChallengeId;
                flowState.ChallengeExpiresAtUtc = startResult.ChallengeExpiresAtUtc;
                flowState.PasskeyAvailable = true;
                flowState.PasskeyEnrollmentRequired = true;
                InteractiveLoginFlowStateStore.Save(TempData, flowState);
                return Redirect(AuthPageRoutes.Passkey);
            case InteractiveLoginStep.RequiresTrustDevice:
                if (!startResult.ChallengeId.HasValue)
                {
                    ErrorMessage = "Something went wrong. Please try again.";
                    return Page();
                }
                flowState.ChallengeId = startResult.ChallengeId;
                flowState.ChallengeExpiresAtUtc = startResult.ChallengeExpiresAtUtc;
                InteractiveLoginFlowStateStore.Save(TempData, flowState);
                return Redirect(AuthPageRoutes.TrustDevice);
            case InteractiveLoginStep.PasswordExpired:
                if (!startResult.ChallengeId.HasValue)
                {
                    ErrorMessage = "Something went wrong. Please try again.";
                    return Page();
                }
                flowState.ChallengeId = startResult.ChallengeId;
                flowState.ChallengeExpiresAtUtc = startResult.ChallengeExpiresAtUtc;
                InteractiveLoginFlowStateStore.Save(TempData, flowState);
                return Redirect(AuthPageRoutes.PasswordExpired);
            case InteractiveLoginStep.Locked:
                return Redirect($"{AuthPageRoutes.Locked}?reason=locked");
            case InteractiveLoginStep.Blocked:
                return Redirect($"{AuthPageRoutes.Locked}?reason=blocked");
            default:
                ErrorMessage = startResult.Message ?? GenericLoginFailureMessage;
                return Page();
        }
    }

    private string GetOrCreateDeviceFingerprint()
    {
        if (Request.Cookies.TryGetValue(DeviceFingerprintCookieName, out var existing) &&
            !string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var seed = string.Join("|",
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip",
            Request.Headers.UserAgent.ToString(),
            Guid.NewGuid().ToString("N"));

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var fingerprint = Convert.ToHexString(hashBytes).ToLowerInvariant();

        Response.Cookies.Append(
            DeviceFingerprintCookieName,
            fingerprint,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !_isDevelopment || Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(DeviceFingerprintCookieDays),
                IsEssential = true
            });

        return fingerprint;
    }

    public class LoginInputModel
    {
        [StringLength(AuthInputConstraints.TenantMaxLength, MinimumLength = 2)]
        [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Enter a valid Organization ID.")]
        [Display(Name = "Tenant")]
        public string? Tenant { get; set; }

        [Required]
        [StringLength(AuthInputConstraints.LoginInputMaxLength, MinimumLength = 3)]
        [EmailOrUsername]
        [Display(Name = "Username or Email")]
        public string UserNameOrEmailAddress { get; set; } = default!;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(AuthInputConstraints.PasswordMaxLength)]
        public string Password { get; set; } = default!;

        [StringLength(AuthInputConstraints.DeviceFingerprintMaxLength)]
        public string? DeviceFingerprint { get; set; }
        [Display(Name = "Remember me?")]
        public bool RememberMe { get; set; }
    }

    private string BuildModeSwitchUrl()
    {
        var targetMode = IsPlatformMode ? TenantModeValue : PlatformModeValue;
        var builder = new QueryBuilder
        {
            { "mode", targetMode }
        };

        var returnUrl = ReturnUrl;
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            returnUrl = Request.Query["ReturnUrl"];
        }

        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            builder.Add("ReturnUrl", returnUrl);
        }

        return AuthPageRoutes.SignIn + builder.ToQueryString();
    }

    private static InteractiveLoginMode ParseMode(string? mode)
    {
        return string.Equals(mode, PlatformModeValue, StringComparison.OrdinalIgnoreCase)
            ? InteractiveLoginMode.Platform
            : InteractiveLoginMode.Tenant;
    }

    private static string ToModeValue(InteractiveLoginMode mode)
    {
        return mode == InteractiveLoginMode.Platform ? PlatformModeValue : TenantModeValue;
    }
}
