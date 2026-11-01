namespace Permissions.Application.Contracts.PermissionEvaluation;

public interface IPermissionEvaluationAppService
{
    Task<EvaluatePermissionsResultDto> EvaluateAsync(EvaluatePermissionsRequestDto input, CancellationToken cancellationToken = default);
    Task<GetMyPermissionsResultDto> GetMyPermissionsAsync(CancellationToken cancellationToken = default);
    Task<ExplainPermissionResultDto> ExplainAsync(ExplainPermissionRequestDto input, CancellationToken cancellationToken = default);
    Task<ExplainPermissionResultDto> ExplainMyPermissionAsync(ExplainMyPermissionRequestDto input, CancellationToken cancellationToken = default);
}
