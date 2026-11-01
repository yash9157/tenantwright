namespace Identity.Domain.Shared.Account.Risk;

[Flags]
public enum IdentityLoginRiskReasonFlags
{
    None = 0,
    NewDevice = 1 << 0,
    NewIp = 1 << 1,
    GeoShift = 1 << 2,
    FailureBurst = 1 << 3,
    OverrideAllow = 1 << 4,
    OverrideForceMfa = 1 << 5,
    OverrideBlock = 1 << 6,
    Unknown = 1 << 30
}
