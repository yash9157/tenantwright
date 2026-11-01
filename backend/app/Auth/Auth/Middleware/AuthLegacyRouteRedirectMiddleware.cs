using Auth.Routing;

namespace Auth.Middleware;

public sealed class AuthLegacyRouteRedirectMiddleware
{
    private static readonly Dictionary<string, string> LegacyGetRoutes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/Account/Login"] = AuthPageRoutes.SignIn,
            ["/Account/ForgotPassword"] = AuthPageRoutes.ForgotPassword,
            ["/Account/ForgotPassword/Submitted"] = AuthPageRoutes.ForgotPasswordSent,
            ["/Account/ResetPassword"] = AuthPageRoutes.ResetPassword,
            ["/Account/ResetPassword/Result"] = AuthPageRoutes.ResetPasswordResult,
            ["/Account/EmailVerification"] = AuthPageRoutes.EmailVerificationRequest,
            ["/Account/EmailVerification/Resend"] = AuthPageRoutes.EmailVerificationResend,
            ["/Account/EmailVerification/Submitted"] = AuthPageRoutes.EmailVerificationSent,
            ["/Account/ConfirmEmail"] = AuthPageRoutes.ConfirmEmail,
            ["/Account/Login/Mfa"] = AuthPageRoutes.Mfa,
            ["/Account/Login/RecoveryCode"] = AuthPageRoutes.RecoveryCode,
            ["/Account/Login/TrustDevice"] = AuthPageRoutes.TrustDevice,
            ["/Account/PasswordExpired"] = AuthPageRoutes.PasswordExpired,
            ["/Account/PasswordChangedSuccess"] = AuthPageRoutes.PasswordChanged,
            ["/Account/Locked"] = AuthPageRoutes.Locked,
            ["/Account/SessionExpired"] = AuthPageRoutes.SessionExpired,
            ["/Account/Logout"] = AuthPageRoutes.Logout,
            ["/Account/Login/Processing"] = AuthPageRoutes.SignIn,
            ["/Account/Register"] = AuthPageRoutes.CreateAccount,
        };

    private readonly RequestDelegate _next;

    public AuthLegacyRouteRedirectMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            if (LegacyGetRoutes.TryGetValue(context.Request.Path.Value ?? string.Empty, out var canonicalPath))
            {
                context.Response.Redirect(AuthPageRoutes.WithQueryString(canonicalPath, context.Request.Query), permanent: false);
                return Task.CompletedTask;
            }
        }

        return _next(context);
    }
}
