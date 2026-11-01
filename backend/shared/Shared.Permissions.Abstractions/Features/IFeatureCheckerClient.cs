namespace Permissions.Application.Contracts.Features;

public interface IFeatureCheckerClient
{
    Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default);

    Task<Dictionary<string, bool>> AreEnabledAsync(Guid? tenantId, IReadOnlyCollection<string> featureNames, CancellationToken cancellationToken = default);
}
