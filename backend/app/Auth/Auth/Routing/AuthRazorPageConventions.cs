using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Routing;

public static class AuthRazorPageConventions
{
    public static readonly IReadOnlyList<string> PublicAnonymousAccountPages =
    [
        "/Account/Login",
        "/Account/CreateAccount",
        "/Account/CreateAccountSuccess",
        "/Account/PublicRegistrationStatus",
        "/Account/AcceptInvite",
        "/Account/ForgotPassword",
        "/Account/ForgotPasswordSubmitted",
        "/Account/ResetPassword",
        "/Account/ResetPasswordResult",
        "/Account/EmailVerification",
        "/Account/EmailVerificationResend",
        "/Account/EmailVerificationSubmitted",
        "/Account/ConfirmEmail",
        "/Account/Mfa",
        "/Account/Passkey",
        "/Account/RecoveryCode",
        "/Account/TrustDevice",
        "/Account/PasswordExpired",
        "/Account/PasswordChangedSuccess",
        "/Account/SessionExpired",
        "/Account/Locked",
        "/Account/AccessDenied",
        "/Account/Maintenance",
        "/Account/NotFound"
    ];

    public static void Apply(RazorPagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Conventions.AddPageRoute("/Account/Login", AuthPageRoutes.SignIn.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/CreateAccount", AuthPageRoutes.CreateAccount.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/CreateAccountSuccess", AuthPageRoutes.CreateAccountSuccess.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/PublicRegistrationStatus", AuthPageRoutes.PublicRegistrationStatus.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/AcceptInvite", AuthPageRoutes.AcceptInvite.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/ForgotPassword", AuthPageRoutes.ForgotPassword.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/ForgotPasswordSubmitted", AuthPageRoutes.ForgotPasswordSent.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/ResetPassword", AuthPageRoutes.ResetPassword.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/ResetPasswordResult", AuthPageRoutes.ResetPasswordResult.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/EmailVerification", AuthPageRoutes.EmailVerificationRequest.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/EmailVerificationResend", AuthPageRoutes.EmailVerificationResend.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/EmailVerificationSubmitted", AuthPageRoutes.EmailVerificationSent.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/ConfirmEmail", AuthPageRoutes.ConfirmEmail.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/Mfa", AuthPageRoutes.Mfa.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/Passkey", AuthPageRoutes.Passkey.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/RecoveryCode", AuthPageRoutes.RecoveryCode.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/TrustDevice", AuthPageRoutes.TrustDevice.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/PasswordExpired", AuthPageRoutes.PasswordExpired.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/PasswordChangedSuccess", AuthPageRoutes.PasswordChanged.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/SessionExpired", AuthPageRoutes.SessionExpired.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/Locked", AuthPageRoutes.Locked.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/Logout", AuthPageRoutes.Logout.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/AccessDenied", AuthPageRoutes.AccessDenied.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/Maintenance", AuthPageRoutes.Maintenance.TrimStart('/'));
        options.Conventions.AddPageRoute("/Account/NotFound", AuthPageRoutes.NotFound.TrimStart('/'));

        foreach (var page in PublicAnonymousAccountPages)
        {
            options.Conventions.AllowAnonymousToPage(page);
        }
    }
}
