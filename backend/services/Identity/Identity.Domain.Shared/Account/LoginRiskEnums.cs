namespace Identity.Domain.Shared.Account;

public enum LoginRiskLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public enum LoginRiskAction
{
    Allow = 0,
    ForceMfa = 1,
    Block = 2
}
