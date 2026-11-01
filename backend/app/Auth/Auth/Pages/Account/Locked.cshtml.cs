using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account;

public class LockedModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Reason { get; set; }

    public bool IsBlocked =>
        string.Equals(Reason, "blocked", StringComparison.OrdinalIgnoreCase);

    public bool IsLocked =>
        string.Equals(Reason, "locked", StringComparison.OrdinalIgnoreCase);

    public IActionResult OnGet() => Page();
}
