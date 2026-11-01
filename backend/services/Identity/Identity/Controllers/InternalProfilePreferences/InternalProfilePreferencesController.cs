using Identity.Application.Contracts.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.InternalProfilePreferences;

/// <summary>
/// Read-only preference lookups for Notifications' delivery path. A safe GET carries no Idempotency-Key: requiring one
/// would reject every caller with 400, and replaying a cached response would serve a preference the user has since
/// changed.
/// </summary>
[ApiController]
[IntegrationService]
[Route("api/identity/internal/profile-preferences")]
[Authorize(Policy = "IdentityInternal")]
public class InternalProfilePreferencesController : ControllerBase
{
    private readonly IInternalProfilePreferenceLookupAppService _lookupAppService;

    public InternalProfilePreferencesController(IInternalProfilePreferenceLookupAppService lookupAppService)
    {
        _lookupAppService = lookupAppService;
    }

    [HttpGet("users/{userId:guid}/alerts/{alertKey}")]
    public Task<UserSecurityAlertPreferenceStateDto> GetAlertPreferenceAsync(Guid userId, string alertKey)
    {
        return _lookupAppService.GetAlertPreferenceAsync(userId, alertKey);
    }
}
