using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.PermissionEvaluation;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.PermissionEvaluation;

/// <summary>
/// Internal M2M-only current-subject authorization for durable service work. It never accepts role claims from the
/// caller and never returns grants, roles, account data, or the evaluated permission set.
/// </summary>
[ApiController]
[IntegrationService]
[Route("api/permissions/internal/subject-permission-authorizations")]
[Authorize(Policy = PermissionsHostPolicies.SubjectPermissionAuthorizationInternal)]
public sealed class SubjectPermissionAuthorizationsController(
    ISubjectPermissionAuthorizationAppService service) : ControllerBase
{
    [HttpPost]
    public Task<SubjectPermissionAuthorizationResultDto> AuthorizeCurrentSubjectAsync(
        [FromBody] SubjectPermissionAuthorizationRequestDto input,
        CancellationToken cancellationToken) =>
        service.AuthorizeCurrentSubjectAsync(input, cancellationToken);
}
