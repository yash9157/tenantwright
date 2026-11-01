using Identity.Application.Contracts.SecuritySettings;

namespace Identity.Application.Contracts.SecuritySettings;

public interface IHostIdentitySecurityPolicyProvider
{
    Task<HostIdentitySecuritySettingsDto> GetEffectiveAsync(CancellationToken cancellationToken = default);

    Task InvalidateAsync(CancellationToken cancellationToken = default);
}
