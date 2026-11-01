namespace Identity.Application.Contracts.Account.Risk;

public sealed class LoginRiskAlertingOptions
{
    public bool Enabled { get; set; } = true;
    public string TemplateName { get; set; } = "security-risk-blocked-login";
    public string Subject { get; set; } = "High-risk login blocked";
    public List<string> Recipients { get; set; } = [];
}
