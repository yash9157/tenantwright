using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Shared.EventBus.Messaging;

/// <summary>
/// The fail-closed checks every transport provider runs before it connects to the broker: the configured provider is
/// the one starting, this host owns every receive endpoint it is about to consume, the durable consume ports exist,
/// and the active signing key round-trips through the public-key registry under this host's producer principal.
/// </summary>
public static class TenantWrightMessagingProviderPreconditions
{
    public static void Verify(IServiceProvider services, TenantWrightMessageCatalog catalog, TenantWrightMessagingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        var options = services.GetRequiredService<IOptions<MessagingOptions>>().Value;
        if (options.Provider != provider)
            throw new InvalidOperationException(
                $"The {provider} transport requires {TenantWrightMessagingProviderConfiguration.ProviderKey}={provider}.");
        if (catalog.ReceiveEndpoints.Any(endpoint =>
                !string.Equals(endpoint.OwnerPrincipal, options.ProducerPrincipal, StringComparison.Ordinal)))
            throw new InvalidOperationException("Every receive endpoint owner must equal this host's exact producer principal.");

        if (catalog.ReceiveEndpoints.Count > 0)
            RequireConsumeTrustPorts(services);
        VerifySigningConfiguration(services, options);
    }

    private static void RequireConsumeTrustPorts(IServiceProvider services)
    {
        var registrations = services.GetRequiredService<IServiceProviderIsService>();
        if (!registrations.IsService(typeof(IProtectedReplayIdempotencyStore)))
            throw new InvalidOperationException("The durable protected replay idempotency store must be registered.");
        if (!registrations.IsService(typeof(ITenantWrightInboxTransaction)))
            throw new InvalidOperationException("The durable inbox transaction must be registered.");
    }

    private static void VerifySigningConfiguration(IServiceProvider services, MessagingOptions options)
    {
        byte[]? signature = null;
        try
        {
            var signer = services.GetRequiredService<ITenantWrightMessageSigner>();
            var registry = services.GetRequiredService<ITenantWrightPublicKeyRegistry>();
            if (!string.Equals(signer.Actor, options.ProducerPrincipal, StringComparison.Ordinal) ||
                !string.Equals(signer.KeyId, options.Trust.ActiveKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException();

            ReadOnlySpan<byte> proof = "tenantwright-messaging-key-self-check-v1"u8;
            signature = signer.Sign(proof);
            if (!registry.Verify(options.Trust.ActiveKeyId, options.ProducerPrincipal, proof, signature))
                throw new InvalidOperationException();
        }
        catch
        {
            // Key material problems are reported without detail: the cause can echo key paths or provider text.
            throw new InvalidOperationException("Messaging signing key validation failed.");
        }
        finally
        {
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
        }
    }
}
