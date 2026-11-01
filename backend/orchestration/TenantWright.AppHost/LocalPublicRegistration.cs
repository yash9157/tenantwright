using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TenantWright.AppHost;

internal static class LocalPublicRegistration
{
    private const string Section = "LocalOnboarding";

    // Cloudflare's documented always-pass Turnstile test pair; Auth accepts it only in Development.
    private const string TurnstileTestSiteKey = "1x00000000000000000000AA";
    private const string TurnstileTestSecret = "1x0000000000000000000000000000000AA";

    /// <summary>
    /// Enables self-service tenant registration on Auth with the Turnstile test pair and a generated hash key.
    /// <c>LocalOnboarding:PublicRegistrationEnabled</c> defaults to whether local messaging is enabled, because a
    /// registration is completed by the message-driven onboarding workflow.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithLocalPublicRegistration(
        this IResourceBuilder<ProjectResource> auth,
        IConfiguration configuration,
        IHostEnvironment environment,
        LocalInfrastructure infrastructure,
        IResourceBuilder<ParameterResource> hashKey)
    {
        var enabled = configuration.GetValue<bool?>($"{Section}:PublicRegistrationEnabled") ?? infrastructure.Messaging.Enabled;
        if (!enabled)
        {
            return auth;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Local public-registration test credentials may only be used in Development.");
        }

        if (!infrastructure.Messaging.Enabled)
        {
            throw new InvalidOperationException(
                $"{Section}:PublicRegistrationEnabled requires local messaging; onboarding cannot complete with LocalMessaging:Provider=None.");
        }

        return auth
            .WithEnvironment("Auth__PublicTenantRegistration__Enabled", "true")
            .WithEnvironment("Auth__PublicTenantRegistration__TurnstileSiteKey", TurnstileTestSiteKey)
            .WithEnvironment("Auth__PublicTenantRegistration__TurnstileSecret", TurnstileTestSecret)
            .WithEnvironment("Auth__PublicTenantRegistration__ExpectedHostName", string.Empty)
            .WithEnvironment("Auth__PublicTenantRegistration__HashKey", hashKey);
    }
}
