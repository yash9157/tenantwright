using Identity.Domain.Shared.Account.Risk;

namespace Identity.Application.Contracts.Account.Risk;

public sealed class LoginRiskOptions
{
    public bool Enabled { get; set; } = true;
    public LoginRiskMode Mode { get; set; } = LoginRiskMode.Observe;

    public int LowThreshold { get; set; } = 40;
    public int HighThreshold { get; set; } = 70;

    public int NewDeviceWeight { get; set; } = 25;
    public int NewIpWeight { get; set; } = 20;
    public int GeoShiftWeight { get; set; }
    public bool GeoSignalTrusted { get; set; }
    public int FailureBurstWeight { get; set; } = 25;

    public TimeSpan FailureBurstWindow { get; set; } = TimeSpan.FromMinutes(15);
    public int FailureBurstThreshold { get; set; } = 3;
    public TimeSpan HistoryWindow { get; set; } = TimeSpan.FromDays(30);

    public int TrustedDeviceDays { get; set; } = 30;
    public string HeaderCountryCodeName { get; set; } = "X-Country-Code";
}
