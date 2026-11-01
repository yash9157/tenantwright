namespace Identity.Application.Contracts.Account.Risk;

public sealed class LoginRiskEvaluationContext
{
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? DeviceFingerprint { get; set; }
}
