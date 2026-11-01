using Auth.Registration;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account;

public sealed class PublicRegistrationStatusModel : PageModel
{
    private readonly IPublicRegistrationStatusService _statusService;

    public PublicRegistrationStatusModel(IPublicRegistrationStatusService statusService) =>
        _statusService = statusService;

    public PublicRegistrationStatusView Status { get; private set; } =
        PublicRegistrationStatusView.Unavailable();

    public string Token { get; private set; } = string.Empty;

    public async Task OnGetAsync(string? token)
    {
        Token = token is { Length: <= 2048 } ? token : string.Empty;
        Status = await _statusService.GetAsync(Token, HttpContext.RequestAborted);
    }
}
