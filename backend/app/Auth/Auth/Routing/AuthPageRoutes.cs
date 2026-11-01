using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace Auth.Routing;

public static class AuthPageRoutes
{
    public const string SignIn = "/auth/sign-in";
    public const string CreateAccount = "/auth/create-account";
    public const string CreateAccountSuccess = "/auth/create-account/success";
    public const string PublicRegistrationStatus = "/auth/create-account/status";
    public const string AcceptInvite = "/auth/accept-invite";
    public const string ForgotPassword = "/auth/forgot-password";
    public const string ForgotPasswordSent = "/auth/forgot-password/sent";
    public const string ResetPassword = "/auth/reset-password";
    public const string ResetPasswordResult = "/auth/reset-password/result";
    public const string EmailVerificationRequest = "/auth/email-verification/request";
    public const string EmailVerificationResend = "/auth/email-verification/resend";
    public const string EmailVerificationSent = "/auth/email-verification/sent";
    public const string ConfirmEmail = "/auth/email-verification/confirm";
    public const string Mfa = "/auth/mfa";
    public const string Passkey = "/auth/passkey";
    public const string RecoveryCode = "/auth/recovery-code";
    public const string TrustDevice = "/auth/trust-device";
    public const string PasswordExpired = "/auth/password-expired";
    public const string PasswordChanged = "/auth/password-updated";
    public const string AccessDenied = "/auth/access-denied";
    public const string SessionExpired = "/auth/session-expired";
    public const string Locked = "/auth/locked";
    public const string Logout = "/auth/logout";
    public const string Maintenance = "/auth/maintenance";
    public const string NotFound = "/auth/not-found";

    public static string WithQueryString(string path, IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return path;
        }

        var builder = new QueryBuilder();
        foreach (var pair in query)
        {
            foreach (var value in pair.Value)
            {
                if (value is not null)
                {
                    builder.Add(pair.Key, value);
                }
            }
        }

        return path + builder.ToQueryString();
    }
}
