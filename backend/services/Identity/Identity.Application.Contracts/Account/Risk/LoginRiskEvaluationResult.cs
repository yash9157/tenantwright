using Identity.Domain.Shared.Account;

namespace Identity.Application.Contracts.Account.Risk;

public sealed class LoginRiskEvaluationResult
{
    public LoginRiskLevel Level { get; set; } = LoginRiskLevel.None;
    public LoginRiskAction Action { get; set; } = LoginRiskAction.Allow;
    public int Score { get; set; }
    public List<string> Reasons { get; set; } = [];
    public string? CountryCode { get; set; }
    public Guid? AppliedOverrideId { get; set; }
}
