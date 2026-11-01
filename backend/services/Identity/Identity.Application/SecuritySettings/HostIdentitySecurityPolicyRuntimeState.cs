using Identity.Application.Contracts.SecuritySettings;

namespace Identity.Application.SecuritySettings;

public sealed class HostIdentitySecurityPolicyRuntimeState
{
    private readonly object _sync = new();
    private HostIdentitySecuritySettingsDto _current;

    public HostIdentitySecurityPolicyRuntimeState(HostIdentitySecurityStaticOptions staticOptions)
    {
        ArgumentNullException.ThrowIfNull(staticOptions);
        _current = HostIdentitySecuritySettingsMapper.Clone(staticOptions.DefaultSettings);
    }

    public HostIdentitySecuritySettingsDto GetCurrent()
    {
        lock (_sync)
        {
            return HostIdentitySecuritySettingsMapper.Clone(_current);
        }
    }

    public void SetCurrent(HostIdentitySecuritySettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            _current = HostIdentitySecuritySettingsMapper.Clone(settings);
        }
    }
}
