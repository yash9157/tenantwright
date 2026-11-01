using Auth.Authorization;
using Auth.Managers;
using Auth.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.AspNetCore.Host.Idempotency;
using Shared.Permissions.Definitions.Auth;

namespace Auth.Controllers.Management;

[ApiController]
[Route("api/management/openiddict")]
[Authorize(
    AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    Policy = OpenIddictManagementPolicies.Default)]
public sealed class OpenIddictManagementController : ControllerBase
{
    private readonly OpenIddictManagementManager _manager;

    public OpenIddictManagementController(OpenIddictManagementManager manager)
    {
        _manager = manager;
    }

    [HttpGet("metadata")]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.Read)]
    public Task<ActionResult<OpenIddictManagementMetadataModel>> GetMetadataAsync()
        => ExecuteAsync(() => _manager.GetMetadataAsync(HttpContext.RequestAborted));

    [HttpGet("applications")]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.Read)]
    public Task<ActionResult<OpenIddictManagementPage<OpenIddictApplicationModel>>> GetApplicationListAsync(
        [FromQuery] string? search = null,
        [FromQuery] string? clientType = null,
        [FromQuery] bool? configurationManaged = null,
        [FromQuery] int skipCount = 0,
        [FromQuery] int maxResultCount = 50)
        => ExecuteAsync(() => _manager.GetApplicationsAsync(
            search,
            clientType,
            configurationManaged,
            skipCount,
            maxResultCount,
            HttpContext.RequestAborted));

    [HttpGet("applications/{clientId}")]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.Read)]
    public Task<ActionResult<OpenIddictApplicationModel>> GetApplicationAsync(string clientId)
        => ExecuteAsync(() => _manager.GetApplicationAsync(clientId, HttpContext.RequestAborted));

    [HttpGet("applications/{clientId}/impact")]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.Read)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<ActionResult<OpenIddictApplicationImpactModel>> GetApplicationImpactAsync(string clientId)
        => ExecuteAsync(() => _manager.GetApplicationImpactAsync(
            clientId,
            GetCurrentRequestClientId(),
            HttpContext.RequestAborted));

    [HttpPost("applications")]
    [EnableRateLimiting("openiddict-management")]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.Create)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<ActionResult<OpenIddictApplicationSecretResult>> CreateApplicationAsync(
        [FromBody] OpenIddictApplicationCreateInput input)
        => ExecuteAsync(() => _manager.CreateApplicationAsync(input, GetActor(), HttpContext.RequestAborted));

    [HttpPut("applications/{clientId}")]
    [EnableRateLimiting("openiddict-management")]
    [RequireIdempotency]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.Update)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<ActionResult<OpenIddictApplicationModel>> UpdateApplicationAsync(
        string clientId,
        [FromBody] OpenIddictApplicationUpdateInput input)
        => ExecuteAsync(() => _manager.UpdateApplicationAsync(clientId, input, GetActor(), HttpContext.RequestAborted));

    [HttpPost("applications/{clientId}/secret/rotate")]
    [EnableRateLimiting("openiddict-management")]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.RotateSecret)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<ActionResult<OpenIddictApplicationSecretResult>> RotateApplicationSecretAsync(
        string clientId,
        [FromBody] RotateOpenIddictApplicationSecretInput input)
        => ExecuteAsync(() => _manager.RotateSecretAsync(
            clientId,
            input.ConcurrencyToken,
            GetActor(),
            HttpContext.RequestAborted));

    [HttpPost("applications/{clientId}/secret/revoke")]
    [EnableRateLimiting("openiddict-management")]
    [RequireIdempotency]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.RevokeSecret)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<ActionResult<OpenIddictApplicationModel>> RevokeApplicationSecretAsync(
        string clientId,
        [FromBody] RevokeOpenIddictApplicationSecretInput input)
        => ExecuteAsync(() => _manager.RevokeSecretAsync(
            clientId,
            input.ConcurrencyToken,
            GetActor(),
            HttpContext.RequestAborted));

    [HttpDelete("applications/{clientId}")]
    [EnableRateLimiting("openiddict-management")]
    [RequireIdempotency]
    [Authorize(Policy = OpenIddictManagementPolicies.Applications.Delete)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<IActionResult> DeleteApplicationAsync(string clientId)
        => ExecuteAsync(() => _manager.DeleteApplicationAsync(
            clientId,
            GetCurrentRequestClientId(),
            GetActor(),
            HttpContext.RequestAborted));

    [HttpGet("scopes")]
    [Authorize(Policy = OpenIddictManagementPolicies.Scopes.Read)]
    public Task<ActionResult<OpenIddictManagementPage<OpenIddictScopeModel>>> GetScopeListAsync(
        [FromQuery] string? search = null,
        [FromQuery] bool? configurationManaged = null,
        [FromQuery] int skipCount = 0,
        [FromQuery] int maxResultCount = 50)
        => ExecuteAsync(() => _manager.GetScopesAsync(
            search,
            configurationManaged,
            skipCount,
            maxResultCount,
            HttpContext.RequestAborted));

    [HttpGet("scopes/{name}")]
    [Authorize(Policy = OpenIddictManagementPolicies.Scopes.Read)]
    public Task<ActionResult<OpenIddictScopeModel>> GetScopeAsync(string name)
        => ExecuteAsync(() => _manager.GetScopeAsync(name, HttpContext.RequestAborted));

    [HttpPost("scopes")]
    [EnableRateLimiting("openiddict-management")]
    [RequireIdempotency]
    [Authorize(Policy = OpenIddictManagementPolicies.Scopes.Create)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<ActionResult<OpenIddictScopeModel>> CreateScopeAsync([FromBody] OpenIddictScopeCreateInput input)
        => ExecuteAsync(() => _manager.CreateScopeAsync(input, GetActor(), HttpContext.RequestAborted));

    [HttpPut("scopes/{name}")]
    [EnableRateLimiting("openiddict-management")]
    [RequireIdempotency]
    [Authorize(Policy = OpenIddictManagementPolicies.Scopes.Update)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<ActionResult<OpenIddictScopeModel>> UpdateScopeAsync(
        string name,
        [FromBody] OpenIddictScopeUpdateInput input)
        => ExecuteAsync(() => _manager.UpdateScopeAsync(name, input, GetActor(), HttpContext.RequestAborted));

    [HttpDelete("scopes/{name}")]
    [EnableRateLimiting("openiddict-management")]
    [RequireIdempotency]
    [Authorize(Policy = OpenIddictManagementPolicies.Scopes.Delete)]
    [Audited(Category = "auth.openiddict-management")]
    public Task<IActionResult> DeleteScopeAsync(string name)
        => ExecuteAsync(() => _manager.DeleteScopeAsync(name, GetActor(), HttpContext.RequestAborted));

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OpenIddictManagementException exception)
        {
            return new ObjectResult(exception.ToModel()) { StatusCode = exception.StatusCode };
        }
    }

    private async Task<IActionResult> ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action();
            return NoContent();
        }
        catch (OpenIddictManagementException exception)
        {
            return StatusCode(exception.StatusCode, exception.ToModel());
        }
    }

    private string GetActor()
    {
        return User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value
            ?? User.Identity?.Name
            ?? "unknown";
    }

    private string? GetCurrentRequestClientId()
        => User.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
}
