namespace Permissions.Application.Contracts.Authorization;

public interface IRequestAuthorizationService
{
    Task EnsureAuthorizedAsync(string policyName, CancellationToken cancellationToken = default);
}
