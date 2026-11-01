using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Shared.EventBus.Messaging;

public static class TenantWrightMessagingRegistration
{
    /// <summary>
    /// Registers the provider-neutral messaging core for <paramref name="catalog"/>: options, signing and verification
    /// keys, attestations, outbound stamping, the inbound trust verifier and pipeline, and protected replay issuance.
    /// The active <see cref="ITenantWrightMessagingProvider"/> registers its own <see cref="ITenantWrightMessagingTransport"/>
    /// and, only when it implements them, its redelivery and replay custody verifiers.
    /// </summary>
    public static IServiceCollection AddTenantWrightMessagingCore(
        this IServiceCollection services,
        IConfiguration configuration,
        TenantWrightMessageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);

        services.AddSingleton(catalog);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidateOptions<MessagingOptions>, MessagingOptionsValidator>();
        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<ITenantWrightMessageSigner, TenantWrightMessageSigner>();
        services.AddSingleton<ITenantWrightPublicKeyRegistry, TenantWrightPublicKeyRegistry>();
        services.AddSingleton<TenantWrightAttestationService>();
        services.AddScoped<TenantWrightOutboundMessageStamper>();
        services.AddScoped<TenantWrightInboundTrustVerifier>();
        services.AddScoped<TenantWrightInboundMessagePipeline>();
        services.AddScoped<ProtectedReplayService>();
        return services;
    }
}
