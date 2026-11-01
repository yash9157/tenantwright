using System.Security.Claims;
using System.Threading.Tasks;
using OpenIddict.Abstractions;
using Shared.Modularity.DependencyInjection;
using Shared.Security;

namespace Auth.Claims;

public interface IOpenIddictClaimsPrincipalHandler
{
    Task HandleAsync(ClaimsPrincipal principal);
}

public class OpenIddictClaimsPrincipalHandler : IOpenIddictClaimsPrincipalHandler, ITransientDependency
{
    public virtual Task HandleAsync(ClaimsPrincipal principal)
    {
        var scopes = principal.GetScopes();
        foreach (var claim in principal.Claims)
        {
            switch (claim.Type)
            {
                case OpenIddictConstants.Claims.Subject:
                    claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken);
                    break;

                case OpenIddictConstants.Claims.Name:
                case OpenIddictConstants.Claims.PreferredUsername:
                case OpenIddictConstants.Claims.GivenName:
                case OpenIddictConstants.Claims.FamilyName:
                    if (scopes.Contains(OpenIddictConstants.Scopes.Profile))
                    {
                        claim.SetDestinations(OpenIddictConstants.Destinations.IdentityToken);
                    }
                    break;

                case OpenIddictConstants.Claims.Email:
                    if (scopes.Contains(OpenIddictConstants.Scopes.Email))
                    {
                        claim.SetDestinations(OpenIddictConstants.Destinations.IdentityToken);
                    }
                    break;

                case OpenIddictConstants.Claims.PhoneNumber:
                    if (scopes.Contains(OpenIddictConstants.Scopes.Phone))
                    {
                        claim.SetDestinations(OpenIddictConstants.Destinations.IdentityToken);
                    }
                    break;

                case OpenIddictConstants.Claims.Role:
                    if (scopes.Contains(OpenIddictConstants.Scopes.Roles))
                    {
                        claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken);
                    }
                    break;

                case SharedClaimTypes.TenantId:
                    claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
                    break;

                case SharedClaimTypes.SessionId:
                    claim.SetDestinations(
                        OpenIddictConstants.Destinations.AccessToken,
                        OpenIddictConstants.Destinations.IdentityToken);
                    break;

                case SharedClaimTypes.IdentityRoleId:
                    if (scopes.Contains(OpenIddictConstants.Scopes.Roles))
                    {
                        claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
                    }
                    break;

                case OpenIddictConstants.Claims.ClientId:
                    claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
                    break;

                case OpenIddictConstants.Claims.AuthenticationMethodReference:
                case OpenIddictConstants.Claims.AuthenticationTime:
                    claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken);
                    break;

                default:
                    break;
            }
        }

        return Task.CompletedTask;
    }
}
