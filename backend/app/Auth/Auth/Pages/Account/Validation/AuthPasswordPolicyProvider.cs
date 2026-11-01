using Microsoft.Extensions.Hosting;

namespace Auth.Pages.Account.Validation;

public sealed class AuthPasswordPolicyProvider : IAuthPasswordPolicyProvider
{
    private readonly AuthPasswordPolicyDescriptor _policy;

    public AuthPasswordPolicyProvider(IHostEnvironment environment)
    {
        var isDevelopment = environment.IsDevelopment();
        _policy = new AuthPasswordPolicyDescriptor
        {
            RequiredLength = isDevelopment ? 6 : 8,
            RequireDigit = !isDevelopment,
            RequireLowercase = !isDevelopment,
            RequireUppercase = !isDevelopment,
            RequireNonAlphanumeric = !isDevelopment
        };
    }

    public AuthPasswordPolicyDescriptor GetPolicy() => _policy;
}
