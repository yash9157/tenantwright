using Identity.Application.Users;
using Microsoft.AspNetCore.DataProtection;

namespace Identity.Security;

internal sealed class IdentityUserImportPayloadProtector : IIdentityUserImportPayloadProtector
{
    private const string Purpose = "TenantWright.Identity.UserImportPayload.v1";
    private readonly IDataProtector _protector;

    public IdentityUserImportPayloadProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext)
    {
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedPayload)
    {
        return _protector.Unprotect(protectedPayload);
    }
}
