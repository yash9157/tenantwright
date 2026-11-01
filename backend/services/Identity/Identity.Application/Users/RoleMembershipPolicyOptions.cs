namespace Identity.Application.Users;

public sealed class RoleMembershipPolicyOptions
{
    public const string SectionName = "RoleMembershipPolicy";
    public const int RequiredMaxActiveRoleMemberships = 32;

    public int MaxActiveRoleMemberships { get; set; } = RequiredMaxActiveRoleMemberships;

    public static bool IsValid(RoleMembershipPolicyOptions options)
        => options.MaxActiveRoleMemberships == RequiredMaxActiveRoleMemberships;
}
