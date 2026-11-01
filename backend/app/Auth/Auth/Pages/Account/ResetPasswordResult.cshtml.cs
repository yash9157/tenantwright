using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account;

public class ResetPasswordResultModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    public bool IsSuccess => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);

    public IActionResult OnGet() => Page();
}
