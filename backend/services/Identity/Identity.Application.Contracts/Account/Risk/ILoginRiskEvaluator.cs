namespace Identity.Application.Contracts.Account.Risk;

public interface ILoginRiskEvaluator
{
    Task<LoginRiskEvaluationResult> EvaluateAsync(
        LoginRiskEvaluationContext context,
        CancellationToken cancellationToken = default);
}
