using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account;

public class AccessDeniedModel : PageModel
{
    public IActionResult OnGet() => Page();
}
