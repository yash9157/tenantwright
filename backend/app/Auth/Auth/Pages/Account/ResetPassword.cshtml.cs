using System.ComponentModel.DataAnnotations;
using Auth.Clients;
using Auth.Routing;
using Auth.Pages.Account.Validation;
using Identity.Application.Contracts.Account;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-reset")]
public class ResetPasswordModel : PageModel
{
    private readonly IIdentityClient _identityClient;
    private readonly IAuthPageValidationService _pageValidationService;

    public ResetPasswordModel(
        IIdentityClient identityClient,
        IAuthPageValidationService pageValidationService,
        IAuthPasswordPolicyProvider passwordPolicyProvider)
    {
        _identityClient = identityClient;
        _pageValidationService = pageValidationService;
        PasswordPolicy = passwordPolicyProvider.GetPolicy();
    }

    [BindProperty]
    public ResetPasswordInput Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public AuthPasswordPolicyDescriptor PasswordPolicy { get; }

    public async Task<IActionResult> OnGetAsync([FromQuery] Guid tenantId, [FromQuery] Guid userId, [FromQuery] string token)
    {
        token = AuthInputConstraints.NormalizeToken(token);
        if (tenantId == Guid.Empty || userId == Guid.Empty || string.IsNullOrWhiteSpace(token))
        {
            return Redirect($"{AuthPageRoutes.ResetPasswordResult}?status=invalid");
        }

        var validation = await _identityClient.ValidatePasswordResetTokenAsync(
            new PasswordResetValidateInput
            {
                TenantId = tenantId,
                UserId = userId,
                Token = token
            },
            tenantId,
            HttpContext.RequestAborted);

        if (!validation.IsValid)
        {
            return Redirect($"{AuthPageRoutes.ResetPasswordResult}?status=invalid");
        }

        Input.TenantId = tenantId;
        Input.UserId = userId;
        Input.Token = token;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Input.Token = AuthInputConstraints.NormalizeToken(Input.Token);
        Input.NewPassword = AuthInputConstraints.NormalizePassword(Input.NewPassword);
        Input.ConfirmPassword = AuthInputConstraints.NormalizePassword(Input.ConfirmPassword);

        ModelState.Clear();
        if (!await this.TryValidateInputAsync(_pageValidationService, Input, nameof(Input), HttpContext.RequestAborted))
        {
            return Page();
        }

        var result = await _identityClient.CompletePasswordResetAsync(
            new PasswordResetCompleteInput
            {
                TenantId = Input.TenantId,
                UserId = Input.UserId,
                Token = Input.Token,
                NewPassword = Input.NewPassword,
                ConfirmPassword = Input.ConfirmPassword
            },
            Input.TenantId,
            HttpContext.RequestAborted);

        if (result.Succeeded)
        {
            return Redirect($"{AuthPageRoutes.ResetPasswordResult}?status=success");
        }

        ErrorMessage = result.Errors.Count > 0
            ? string.Join(Environment.NewLine, result.Errors)
            : "Password reset failed.";
        return Page();
    }

    public sealed class ResetPasswordInput
    {
        public Guid TenantId { get; set; }

        public Guid UserId { get; set; }

        [Required]
        [StringLength(AuthInputConstraints.TokenMaxLength)]
        public string Token { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(AuthInputConstraints.PasswordMaxLength)]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(AuthInputConstraints.PasswordMaxLength)]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
