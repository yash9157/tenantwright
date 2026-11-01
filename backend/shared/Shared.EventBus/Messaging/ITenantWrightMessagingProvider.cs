using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.EventBus.Messaging;

/// <summary>
/// The seam one transport provider implements. A provider registers only what is specific to it: its
/// <see cref="ITenantWrightMessagingTransport"/>, its consumers for the catalog's receive endpoints, the redelivery and
/// replay custody verifiers it can prove, its health check and any setting it alone reads. The host composition
/// registers the neutral messaging core before the provider and the durable outbox and inbox after it, so a provider
/// never registers neutral services itself.
/// </summary>
public interface ITenantWrightMessagingProvider
{
    TenantWrightMessagingProvider Kind { get; }

    void Register(IServiceCollection services, IConfiguration configuration, TenantWrightMessageCatalog catalog);
}

/// <summary>Strict reader for <c>Messaging:Provider</c>, shared by every component that branches on the provider.</summary>
public static class TenantWrightMessagingProviderConfiguration
{
    public const string ProviderKey = MessagingOptions.SectionName + ":" + nameof(MessagingOptions.Provider);

    /// <summary>
    /// The configured provider. An absent or empty value is <see cref="TenantWrightMessagingProvider.None"/>; anything that
    /// is not exactly one provider name (case-insensitive) fails closed, so a numeric value, a combination or a typo
    /// can never select a provider by accident.
    /// </summary>
    public static TenantWrightMessagingProvider Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[ProviderKey];
        if (string.IsNullOrWhiteSpace(configured))
            return TenantWrightMessagingProvider.None;

        var name = configured.Trim();
        if (!name.All(char.IsAsciiLetter) ||
            !Enum.TryParse<TenantWrightMessagingProvider>(name, ignoreCase: true, out var provider) ||
            !Enum.IsDefined(provider))
        {
            throw new InvalidOperationException(
                $"{ProviderKey} '{name}' is not a messaging provider. Use one of: " +
                $"{string.Join(", ", Enum.GetNames<TenantWrightMessagingProvider>())}.");
        }

        return provider;
    }
}
