using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http.Extensions;
using Identity.Domain.Shared.Account;
using Auth.Routing;

namespace Auth.Pages.Account;

public class PasswordChangedSuccessModel : PageModel
{
    public string? ReturnUrl { get; private set; }
    public string? Tenant { get; private set; }
    public string? LoginInput { get; private set; }
    public InteractiveLoginMode Mode { get; private set; } = InteractiveLoginMode.Tenant;
    public string SignInUrl => BuildSignInUrl();

    public void OnGet()
    {
        var state = PasswordChangedSuccessStateStore.Read(TempData, keep: true);
        if (state == null)
        {
            return;
        }

        ReturnUrl = state.ReturnUrl;
        Tenant = state.Tenant;
        LoginInput = state.LoginInput;
        Mode = state.Mode;
    }

    private string BuildSignInUrl()
    {
        var builder = new QueryBuilder();
        if (Mode == InteractiveLoginMode.Platform)
        {
            builder.Add("mode", "platform");
        }

        if (!string.IsNullOrWhiteSpace(ReturnUrl))
        {
            builder.Add("ReturnUrl", ReturnUrl);
        }

        if (!string.IsNullOrWhiteSpace(Tenant))
        {
            builder.Add("Tenant", Tenant);
        }

        if (!string.IsNullOrWhiteSpace(LoginInput))
        {
            builder.Add("LoginInput", LoginInput);
        }

        return AuthPageRoutes.SignIn + builder.ToQueryString();
    }
}
