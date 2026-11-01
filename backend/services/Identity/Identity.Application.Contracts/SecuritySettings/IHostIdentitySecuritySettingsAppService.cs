namespace Identity.Application.Contracts.SecuritySettings;

public interface IHostIdentitySecuritySettingsAppService
{
    Task<HostIdentitySecuritySettingsDto> GetHostAsync();

    Task<HostIdentitySecuritySettingsDto> UpdateHostAsync(UpdateHostIdentitySecuritySettingsInput input);

    Task<HostIdentitySecurityPolicyImpactDto> PreviewHostUpdateAsync(UpdateHostIdentitySecuritySettingsInput input);

    Task<HostIdentitySecuritySettingsDto> UpdateHostGovernedAsync(GovernedHostIdentitySecuritySettingsUpdateInput input);
}
