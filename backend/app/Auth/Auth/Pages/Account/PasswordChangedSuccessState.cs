using Identity.Domain.Shared.Account;

namespace Auth.Pages.Account;

public sealed class PasswordChangedSuccessState
{
    public InteractiveLoginMode Mode { get; set; } = InteractiveLoginMode.Tenant;
    public string? ReturnUrl { get; set; }
    public string? Tenant { get; set; }
    public string? LoginInput { get; set; }
}
