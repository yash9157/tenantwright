using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;

namespace Auth.Pages.Account;

public class ForgotPasswordSubmittedModel : PageModel
{
    public IActionResult OnGet() => Page();
}
