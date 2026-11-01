using System.Security.Claims;

namespace Shared.Security;

public static class SharedClaimTypes
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string Subject = "sub";
    public const string UserName = ClaimTypes.Name;
    public const string PreferredUserName = "preferred_username";
    public const string Name = ClaimTypes.GivenName;
    public const string NameAlt = "name";
    public const string SurName = ClaimTypes.Surname;
    public const string SurNameAlt = "family_name";
    public const string Email = ClaimTypes.Email;
    public const string EmailVerified = "email_verified";
    public const string PhoneNumber = ClaimTypes.MobilePhone;
    public const string PhoneNumberAlt = "phone_number";
    public const string PhoneNumberVerified = "phone_number_verified";
    public const string TenantId = "tenantid";
    public const string LegacyTenantId = "http://schemas.microsoft.com/ws/2008/06/identity/claims/tenantid";
    public const string TenantName = "tenantname";
    public const string LegacyTenantName = "http://schemas.microsoft.com/ws/2008/06/identity/claims/tenantname";
    public const string Role = ClaimTypes.Role;
    public const string RoleAlt = "role";
    public const string SessionId = "session_id";
    public const string IdentityRoleId = "urn:tenantwright:identity-role-id";
}
