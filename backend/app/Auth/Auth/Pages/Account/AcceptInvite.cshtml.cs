using System.ComponentModel.DataAnnotations;
using Auth.Clients;
using Auth.Pages.Account.Validation;
using Auth.Routing;
using Identity.Application.Contracts.Account;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-login")]
public class AcceptInviteModel : PageModel
{
    private readonly IIdentityClient _identityClient;
    private readonly IAuthPageValidationService _pageValidationService;
    private readonly string? _publicBaseUrl;
    private readonly bool _isDevelopment;

    public AcceptInviteModel(
        IIdentityClient identityClient,
        IAuthPageValidationService pageValidationService,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _identityClient = identityClient;
        _pageValidationService = pageValidationService;
        _publicBaseUrl = configuration["Auth:PublicBaseUrl"];
        _isDevelopment = environment.IsDevelopment();
    }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public AcceptInviteInput Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }
    public bool InvitationIsValid { get; private set; }
    public string? InvitationStatusMessage { get; private set; }
    public string TenantName { get; private set; } = string.Empty;
    public string InvitationEmail { get; private set; } = string.Empty;
    public string? InvitationRoleName { get; private set; }
    public AuthPasswordPolicyDescriptor PasswordPolicy { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync([FromServices] IAuthPasswordPolicyProvider passwordPolicyProvider)
    {
        PasswordPolicy = passwordPolicyProvider.GetPolicy();
        Input.Token = AuthInputConstraints.NormalizeToken(Token);
        await LoadInvitationAsync(Input.Token);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromServices] IAuthPasswordPolicyProvider passwordPolicyProvider)
    {
        PasswordPolicy = passwordPolicyProvider.GetPolicy();
        Input.Token = AuthInputConstraints.NormalizeToken(Input.Token);
        Input.Email = AuthInputConstraints.NormalizeRequired(Input.Email, AuthInputConstraints.LoginInputMaxLength);
        Input.UserName = AuthInputConstraints.NormalizeRequired(Input.UserName, AuthInputConstraints.LoginInputMaxLength);
        Input.PhoneNumber = AuthInputConstraints.NormalizeOptional(Input.PhoneNumber, 32);
        Input.Password = AuthInputConstraints.NormalizePassword(Input.Password);
        Input.ConfirmPassword = AuthInputConstraints.NormalizePassword(Input.ConfirmPassword);

        await LoadInvitationAsync(Input.Token);
        if (!InvitationIsValid)
        {
            ErrorMessage = InvitationStatusMessage;
            return Page();
        }

        ModelState.Clear();
        if (!await this.TryValidateInputAsync(_pageValidationService, Input, nameof(Input), HttpContext.RequestAborted))
        {
            return Page();
        }

        var verificationBaseUrl = ResolveVerificationBaseUrl();
        if (string.IsNullOrWhiteSpace(verificationBaseUrl))
        {
            ErrorMessage = "Something went wrong. Please try again.";
            return Page();
        }

        var result = await _identityClient.AcceptPublicInvitationAsync(
            new PublicInvitationAcceptInput
            {
                Token = Input.Token,
                Email = Input.Email,
                UserName = Input.UserName,
                PhoneNumber = Input.PhoneNumber,
                Password = Input.Password,
                ConfirmPassword = Input.ConfirmPassword,
                VerificationBaseUrl = verificationBaseUrl,
                ClientIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                BrowserInfo = Request.Headers.UserAgent.ToString(),
                CorrelationId = HttpContext.TraceIdentifier
            },
            HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ErrorMessage = result.Message ?? "We could not create your account from this invitation.";
            if (result.Errors.Count > 0)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error);
                }
            }

            await LoadInvitationAsync(Input.Token);
            return Page();
        }

        CreateAccountSuccessStateStore.Save(
            TempData,
            new CreateAccountSuccessState
            {
                Flow = "invite",
                Tenant = result.TenantName,
                LoginInput = result.Email,
                Email = result.Email
            });

        return Redirect(AuthPageRoutes.CreateAccountSuccess);
    }

    private async Task LoadInvitationAsync(string? normalizedToken)
    {
        InvitationIsValid = false;
        InvitationStatusMessage = "Invitation link is invalid or expired.";
        TenantName = string.Empty;
        InvitationEmail = string.Empty;
        InvitationRoleName = null;

        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return;
        }

        var validation = await _identityClient.ValidatePublicInvitationAsync(
            new PublicInvitationValidateInput
            {
                Token = normalizedToken
            },
            HttpContext.RequestAborted);

        InvitationStatusMessage = validation.Message ?? InvitationStatusMessage;
        TenantName = validation.TenantName;
        InvitationEmail = validation.Email;
        InvitationRoleName = validation.RoleName;
        InvitationIsValid = validation.Succeeded;

        if (validation.Succeeded)
        {
            Input.Token = normalizedToken;
            Input.Email = validation.Email;
            if (string.IsNullOrWhiteSpace(Input.UserName) && !string.IsNullOrWhiteSpace(validation.SuggestedUserName))
            {
                Input.UserName = validation.SuggestedUserName;
            }
        }
    }

    private string? ResolveVerificationBaseUrl()
    {
        var configured = _publicBaseUrl?.Trim().TrimEnd('/');
        if (Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) &&
            (string.Equals(configuredUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             (_isDevelopment && string.Equals(configuredUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))))
        {
            return configured;
        }

        if (_isDevelopment)
        {
            return $"{Request.Scheme}://{Request.Host}";
        }

        return null;
    }

    public sealed class AcceptInviteInput
    {
        [Required]
        [StringLength(AuthInputConstraints.TokenMaxLength)]
        public string Token { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [StringLength(AuthInputConstraints.LoginInputMaxLength)]
        [Display(Name = "Invitation Email")]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(AuthInputConstraints.LoginInputMaxLength, MinimumLength = 3)]
        [Display(Name = "Username")]
        public string UserName { get; set; } = string.Empty;

        [StringLength(32)]
        [Display(Name = "Phone Number")]
        public string? PhoneNumber { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [StringLength(AuthInputConstraints.PasswordMaxLength)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(AuthInputConstraints.PasswordMaxLength)]
        [Display(Name = "Confirm Password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
