namespace Auth.Claims;

public sealed class AccessTokenSecurityOptions
{
    public const string SectionName = "TokenSecurity";
    public const int RequiredMaxSerializedAccessTokenBytes = 6 * 1024;
    public const int RequiredMaxActiveRoleMemberships = 32;

    public int MaxSerializedAccessTokenBytes { get; set; } = RequiredMaxSerializedAccessTokenBytes;

    public int MaxActiveRoleMemberships { get; set; } = RequiredMaxActiveRoleMemberships;

    public static bool IsValid(AccessTokenSecurityOptions options)
        => options.MaxSerializedAccessTokenBytes == RequiredMaxSerializedAccessTokenBytes
           && options.MaxActiveRoleMemberships == RequiredMaxActiveRoleMemberships;
}
