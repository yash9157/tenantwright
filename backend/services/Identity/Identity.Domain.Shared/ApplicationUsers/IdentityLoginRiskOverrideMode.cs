namespace Identity.Domain.Shared.ApplicationUsers;

public enum IdentityLoginRiskOverrideMode
{
    Allow = 0,
    ForceMfa = 1,
    Block = 2
}
