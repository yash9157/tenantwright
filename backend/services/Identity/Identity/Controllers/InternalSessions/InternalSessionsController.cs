using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;
using Shared.AspNetCore.Host.Idempotency;

namespace Identity.Controllers.InternalSessions;

[ApiController]
[IntegrationService]
[Route("api/identity/internal/sessions")]
[Authorize(Policy = "IdentityInternal")]
public class InternalSessionsController : ControllerBase
{
    private readonly IIdentitySessionAppService _sessionAppService;

    public InternalSessionsController(IIdentitySessionAppService sessionAppService)
    {
        _sessionAppService = sessionAppService;
    }

    [HttpGet("list")]
    public Task<List<IdentitySessionDto>> GetListAsync([Required] Guid userId, [Required] Guid tenantId)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty)
        {
            return Task.FromResult(new List<IdentitySessionDto>());
        }

        return _sessionAppService.GetListByUserIdAsync(userId, tenantId);
    }

    [HttpGet("active-count")]
    public async Task<ActionResult<TenantActiveSessionCountResponse>> GetActiveCountAsync([Required] Guid tenantId)
    {
        var count = await _sessionAppService.CountActiveByTenantIdAsync(tenantId);
        return Ok(new TenantActiveSessionCountResponse { TenantId = tenantId, ActiveSessionCount = count });
    }

    [HttpPost("validate")]
    public async Task<ActionResult<bool>> ValidateAsync([FromBody] ValidateSessionInput input)
    {
        return await _sessionAppService.ValidateAsync(input.UserId, input.SessionId, input.TenantId);
    }

    [HttpPost("revoke")]
    [RequireIdempotency]
    public async Task<IActionResult> RevokeAsync([FromBody] RevokeSessionInput input)
    {
        if (input.UserId == Guid.Empty || input.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(input.SessionId))
        {
            return Ok();
        }

        await _sessionAppService.RevokeBySessionIdAsync(input.UserId, input.SessionId, input.TenantId);
        return Ok();
    }
}

public sealed class TenantActiveSessionCountResponse
{
    public Guid TenantId { get; set; }
    public int ActiveSessionCount { get; set; }
}
