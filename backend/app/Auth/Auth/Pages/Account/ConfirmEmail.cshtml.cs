using Auth.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account;

public class ConfirmEmailModel : PageModel
{
    private readonly IIdentityClient _identityClient;

    public ConfirmEmailModel(IIdentityClient identityClient)
    {
        _identityClient = identityClient;
    }

    public bool IsSuccess { get; private set; }
    public string Message { get; private set; } = "Email verification failed.";

    public async Task<IActionResult> OnGetAsync([FromQuery] Guid tenantId, [FromQuery] Guid userId, [FromQuery] string token)
    {
        token = AuthInputConstraints.NormalizeToken(token);
        if (tenantId == Guid.Empty || userId == Guid.Empty || string.IsNullOrWhiteSpace(token))
        {
            IsSuccess = false;
            Message = "Invalid verification link.";
            return Page();
        }

        var result = await _identityClient.ConfirmEmailVerificationAsync(
            new Identity.Application.Contracts.Account.EmailVerificationConfirmInput
            {
                TenantId = tenantId,
                UserId = userId,
                Token = token
            },
            tenantId,
            HttpContext.RequestAborted);

        IsSuccess = result.Succeeded;
        Message = result.Message ?? (result.Succeeded ? "Email verified successfully." : "Email verification failed.");
        return Page();
    }
}
