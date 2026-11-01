using Auth.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account;

public class CreateAccountSuccessModel : PageModel
{
    public CreateAccountSuccessState State { get; private set; } = new();

    public bool HasState => !string.IsNullOrWhiteSpace(State.Email);

    public IActionResult OnGet()
    {
        var state = CreateAccountSuccessStateStore.Read(TempData, keep: true);
        if (state == null)
        {
            return Redirect(AuthPageRoutes.SignIn);
        }

        State = state;
        return Page();
    }
}
