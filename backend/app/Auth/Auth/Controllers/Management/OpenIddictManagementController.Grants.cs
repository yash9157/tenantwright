using Auth.Authorization;
using Auth.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Auth;

namespace Auth.Controllers.Management;

[ApiController]
[Route("api/management/openiddict")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public sealed class OpenIddictGrantManagementController : ControllerBase
{
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly IOpenIddictTokenManager _tokenManager;

    public OpenIddictGrantManagementController(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictTokenManager tokenManager)
    {
        _applicationManager = applicationManager;
        _authorizationManager = authorizationManager;
        _tokenManager = tokenManager;
    }
    [HttpGet("authorizations")]
    [Authorize(Policy = OpenIddictManagementPolicies.Authorizations.Read)]
    [Audited(Category = "auth.openiddict-grant")]
    public async Task<ActionResult<OpenIddictManagementPage<OpenIddictAuthorizationModel>>> GetAuthorizationListAsync(
        [FromQuery] string? subject = null,
        [FromQuery] string? clientId = null,
        [FromQuery] int skipCount = 0,
        [FromQuery] int maxResultCount = 50)
    {
        subject = NormalizeFilter(subject);
        clientId = NormalizeFilter(clientId);
        if (subject == null && clientId == null)
        {
            return BadRequest("Subject or clientId is required.");
        }
        if (!IsValidLength(subject, 200) || !IsValidLength(clientId, 100) || !IsValidPage(skipCount, maxResultCount))
        {
            return BadRequest("Invalid authorization filter or page bounds.");
        }

        var applicationId = await ResolveApplicationIdAsync(clientId);
        if (clientId != null && applicationId == null)
        {
            return EmptyPage<OpenIddictAuthorizationModel>(skipCount, maxResultCount);
        }

        var source = subject != null
            ? _authorizationManager.FindBySubjectAsync(subject, HttpContext.RequestAborted)
            : _authorizationManager.FindByApplicationIdAsync(applicationId!, HttpContext.RequestAborted);
        var rows = new List<OpenIddictAuthorizationModel>(maxResultCount + 1);
        var matched = 0;
        var clientIds = new Dictionary<string, string?>(StringComparer.Ordinal);

        await foreach (var authorization in source.WithCancellation(HttpContext.RequestAborted))
        {
            var rowApplicationId = await _authorizationManager.GetApplicationIdAsync(authorization, HttpContext.RequestAborted);
            if (applicationId != null && !string.Equals(applicationId, rowApplicationId, StringComparison.Ordinal))
            {
                continue;
            }
            if (matched++ < skipCount)
            {
                continue;
            }
            rows.Add(await MapAuthorizationAsync(authorization, rowApplicationId, clientIds));
            if (rows.Count > maxResultCount)
            {
                break;
            }
        }

        return Page(rows, skipCount, maxResultCount);
    }

    [HttpPost("authorizations/{id}/revoke")]
    [Authorize(Policy = OpenIddictManagementPolicies.Authorizations.Revoke)]
    [Audited(Category = "auth.openiddict-grant")]
    public async Task<ActionResult<OpenIddictRevocationResult>> RevokeAuthorizationAsync(string id)
    {
        if (!IsValidIdentifier(id))
        {
            return BadRequest("A valid authorization id is required.");
        }
        var authorization = await _authorizationManager.FindByIdAsync(id, HttpContext.RequestAborted);
        if (authorization == null)
        {
            return NotFound();
        }
        var previous = await _authorizationManager.GetStatusAsync(authorization, HttpContext.RequestAborted);
        if (string.Equals(previous, OpenIddictConstants.Statuses.Revoked, StringComparison.Ordinal))
        {
            return Revoked(id, previous, wasAlreadyRevoked: true);
        }

        var succeeded = await _authorizationManager.TryRevokeAsync(authorization, HttpContext.RequestAborted);
        var current = await _authorizationManager.GetStatusAsync(authorization, HttpContext.RequestAborted);
        if (!succeeded && !string.Equals(current, OpenIddictConstants.Statuses.Revoked, StringComparison.Ordinal))
        {
            return Conflict("The authorization could not be revoked because it changed concurrently.");
        }
        return Revoked(id, previous, wasAlreadyRevoked: !succeeded);
    }

    [HttpGet("tokens")]
    [Authorize(Policy = OpenIddictManagementPolicies.Tokens.Read)]
    [Audited(Category = "auth.openiddict-grant")]
    public async Task<ActionResult<OpenIddictManagementPage<OpenIddictTokenModel>>> GetTokenListAsync(
        [FromQuery] string? subject = null,
        [FromQuery] string? clientId = null,
        [FromQuery] string? authorizationId = null,
        [FromQuery] int skipCount = 0,
        [FromQuery] int maxResultCount = 50)
    {
        subject = NormalizeFilter(subject);
        clientId = NormalizeFilter(clientId);
        authorizationId = NormalizeFilter(authorizationId);
        if (subject == null && clientId == null && authorizationId == null)
        {
            return BadRequest("Subject, clientId, or authorizationId is required.");
        }
        if (!IsValidLength(subject, 200) || !IsValidLength(clientId, 100) ||
            !IsValidLength(authorizationId, 100) || !IsValidPage(skipCount, maxResultCount))
        {
            return BadRequest("Invalid token filter or page bounds.");
        }

        var applicationId = await ResolveApplicationIdAsync(clientId);
        if (clientId != null && applicationId == null)
        {
            return EmptyPage<OpenIddictTokenModel>(skipCount, maxResultCount);
        }

        var source = authorizationId != null
            ? _tokenManager.FindByAuthorizationIdAsync(authorizationId, HttpContext.RequestAborted)
            : subject != null
                ? _tokenManager.FindBySubjectAsync(subject, HttpContext.RequestAborted)
                : _tokenManager.FindByApplicationIdAsync(applicationId!, HttpContext.RequestAborted);
        var rows = new List<OpenIddictTokenModel>(maxResultCount + 1);
        var matched = 0;
        var clientIds = new Dictionary<string, string?>(StringComparer.Ordinal);

        await foreach (var token in source.WithCancellation(HttpContext.RequestAborted))
        {
            var rowApplicationId = await _tokenManager.GetApplicationIdAsync(token, HttpContext.RequestAborted);
            var rowAuthorizationId = await _tokenManager.GetAuthorizationIdAsync(token, HttpContext.RequestAborted);
            var rowSubject = await _tokenManager.GetSubjectAsync(token, HttpContext.RequestAborted);
            if ((applicationId != null && !string.Equals(applicationId, rowApplicationId, StringComparison.Ordinal)) ||
                (authorizationId != null && !string.Equals(authorizationId, rowAuthorizationId, StringComparison.Ordinal)) ||
                (subject != null && !string.Equals(subject, rowSubject, StringComparison.Ordinal)))
            {
                continue;
            }
            if (matched++ < skipCount)
            {
                continue;
            }
            rows.Add(await MapTokenAsync(token, rowApplicationId, rowAuthorizationId, rowSubject, clientIds));
            if (rows.Count > maxResultCount)
            {
                break;
            }
        }

        return Page(rows, skipCount, maxResultCount);
    }

    [HttpPost("tokens/{id}/revoke")]
    [Authorize(Policy = OpenIddictManagementPolicies.Tokens.Revoke)]
    [Audited(Category = "auth.openiddict-grant")]
    public async Task<ActionResult<OpenIddictRevocationResult>> RevokeTokenAsync(string id)
    {
        if (!IsValidIdentifier(id))
        {
            return BadRequest("A valid token id is required.");
        }
        var token = await _tokenManager.FindByIdAsync(id, HttpContext.RequestAborted);
        if (token == null)
        {
            return NotFound();
        }
        var previous = await _tokenManager.GetStatusAsync(token, HttpContext.RequestAborted);
        if (string.Equals(previous, OpenIddictConstants.Statuses.Revoked, StringComparison.Ordinal))
        {
            return Revoked(id, previous, wasAlreadyRevoked: true);
        }

        var succeeded = await _tokenManager.TryRevokeAsync(token, HttpContext.RequestAborted);
        var current = await _tokenManager.GetStatusAsync(token, HttpContext.RequestAborted);
        if (!succeeded && !string.Equals(current, OpenIddictConstants.Statuses.Revoked, StringComparison.Ordinal))
        {
            return Conflict("The token could not be revoked because it changed concurrently.");
        }
        return Revoked(id, previous, wasAlreadyRevoked: !succeeded);
    }

    [HttpPost("grants/revoke-by-subject")]
    [Authorize(Policy = OpenIddictManagementPolicies.Authorizations.Revoke)]
    [Authorize(Policy = OpenIddictManagementPolicies.Tokens.Revoke)]
    [Audited(Category = "auth.openiddict-grant")]
    public async Task<ActionResult<OpenIddictSubjectGrantRevocationResult>> RevokeSubjectGrantsAsync(
        [FromBody] RevokeOpenIddictSubjectGrantsInput input)
    {
        var subject = NormalizeFilter(input.Subject);
        var confirmation = NormalizeFilter(input.SubjectConfirmation);
        var reasonCode = NormalizeFilter(input.ReasonCode);
        var note = NormalizeFilter(input.Note);
        if (!IsValidLength(subject, 200) || !IsValidLength(confirmation, 200) ||
            !IsValidLength(reasonCode, 64) || !IsValidLength(note, 500) ||
            subject == null || confirmation == null || reasonCode == null)
        {
            return BadRequest("Subject, confirmation, and bounded reason metadata are required.");
        }
        if (!string.Equals(subject, confirmation, StringComparison.Ordinal))
        {
            return BadRequest("Subject confirmation does not match.");
        }

        // Revoke token rows first, then their parent authorizations. Either step
        // only makes access more restrictive; the operation is safe to retry if
        // a transient failure leaves a partially completed containment action.
        var tokenCount = await _tokenManager.RevokeBySubjectAsync(subject, HttpContext.RequestAborted);
        var authorizationCount = await _authorizationManager.RevokeBySubjectAsync(subject, HttpContext.RequestAborted);
        return new OpenIddictSubjectGrantRevocationResult
        {
            Subject = subject,
            RevokedAuthorizationCount = authorizationCount,
            RevokedTokenCount = tokenCount,
            WasAlreadyRevoked = authorizationCount == 0 && tokenCount == 0
        };
    }

    private async Task<OpenIddictAuthorizationModel> MapAuthorizationAsync(
        object authorization,
        string? applicationId,
        Dictionary<string, string?> clientIds)
    {
        return new OpenIddictAuthorizationModel
        {
            Id = await _authorizationManager.GetIdAsync(authorization, HttpContext.RequestAborted) ?? string.Empty,
            ClientId = await ResolveClientIdAsync(applicationId, clientIds),
            Subject = await _authorizationManager.GetSubjectAsync(authorization, HttpContext.RequestAborted),
            Status = await _authorizationManager.GetStatusAsync(authorization, HttpContext.RequestAborted),
            Type = await _authorizationManager.GetTypeAsync(authorization, HttpContext.RequestAborted),
            Scopes = [.. await _authorizationManager.GetScopesAsync(authorization, HttpContext.RequestAborted)]
        };
    }

    private async Task<OpenIddictTokenModel> MapTokenAsync(
        object token,
        string? applicationId,
        string? authorizationId,
        string? subject,
        Dictionary<string, string?> clientIds)
    {
        return new OpenIddictTokenModel
        {
            Id = await _tokenManager.GetIdAsync(token, HttpContext.RequestAborted) ?? string.Empty,
            AuthorizationId = authorizationId,
            ClientId = await ResolveClientIdAsync(applicationId, clientIds),
            Subject = subject,
            Status = await _tokenManager.GetStatusAsync(token, HttpContext.RequestAborted),
            Type = await _tokenManager.GetTypeAsync(token, HttpContext.RequestAborted),
            CreationDate = await _tokenManager.GetCreationDateAsync(token, HttpContext.RequestAborted),
            ExpirationDate = await _tokenManager.GetExpirationDateAsync(token, HttpContext.RequestAborted),
            RedemptionDate = await _tokenManager.GetRedemptionDateAsync(token, HttpContext.RequestAborted)
        };
    }

    private async Task<string?> ResolveApplicationIdAsync(string? clientId)
    {
        if (clientId == null)
        {
            return null;
        }
        var application = await _applicationManager.FindByClientIdAsync(clientId, HttpContext.RequestAborted);
        return application == null
            ? null
            : await _applicationManager.GetIdAsync(application, HttpContext.RequestAborted);
    }

    private async Task<string?> ResolveClientIdAsync(
        string? applicationId,
        Dictionary<string, string?> cache)
    {
        if (applicationId == null)
        {
            return null;
        }
        if (cache.TryGetValue(applicationId, out var clientId))
        {
            return clientId;
        }
        var application = await _applicationManager.FindByIdAsync(applicationId, HttpContext.RequestAborted);
        clientId = application == null
            ? null
            : await _applicationManager.GetClientIdAsync(application, HttpContext.RequestAborted);
        cache[applicationId] = clientId;
        return clientId;
    }

    private static ActionResult<OpenIddictManagementPage<T>> EmptyPage<T>(int skipCount, int maxResultCount)
        => new OpenIddictManagementPage<T> { SkipCount = skipCount, MaxResultCount = maxResultCount };

    private static OpenIddictManagementPage<T> Page<T>(List<T> rows, int skipCount, int maxResultCount)
    {
        var hasMore = rows.Count > maxResultCount;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return new OpenIddictManagementPage<T>
        {
            SkipCount = skipCount,
            MaxResultCount = maxResultCount,
            HasMore = hasMore,
            Items = rows
        };
    }

    private static OpenIddictRevocationResult Revoked(string id, string? previousStatus, bool wasAlreadyRevoked)
        => new()
        {
            Id = id,
            PreviousStatus = previousStatus,
            CurrentStatus = OpenIddictConstants.Statuses.Revoked,
            WasAlreadyRevoked = wasAlreadyRevoked
        };

    private static bool IsValidPage(int skipCount, int maxResultCount)
        => skipCount is >= 0 and <= 100_000 && maxResultCount is >= 1 and <= 100;

    private static bool IsValidIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 100;

    private static bool IsValidLength(string? value, int maxLength)
        => value == null || value.Length <= maxLength;

    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
