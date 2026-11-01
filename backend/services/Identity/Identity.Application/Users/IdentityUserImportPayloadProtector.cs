namespace Identity.Application.Users;

internal interface IIdentityUserImportPayloadProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedPayload);
}
