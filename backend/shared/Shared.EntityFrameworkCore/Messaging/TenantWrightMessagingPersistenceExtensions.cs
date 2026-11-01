using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EventBus.Messaging;

namespace Shared.EntityFrameworkCore.Messaging;

public static class TenantWrightMessagingPersistenceExtensions
{
    /// <summary>
    /// Registers the provider-neutral durable messaging of one host over <typeparamref name="TDbContext"/>: the outbox
    /// sender, the outbox dispatcher and its retention, the inbox transaction and its retention, and the protected
    /// replay idempotency store. The active transport provider supplies <see cref="ITenantWrightMessagingTransport"/>.
    /// </summary>
    public static IServiceCollection AddTenantWrightMessagingPersistence<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TDbContext : DbContext, IEfCoreDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidateOptions<TenantWrightOutboxOptions>, TenantWrightOutboxOptionsValidator>();
        services.AddOptions<TenantWrightOutboxOptions>()
            .Bind(configuration.GetSection(TenantWrightOutboxOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TenantWrightInboxOptions>, TenantWrightInboxOptionsValidator>();
        services.AddOptions<TenantWrightInboxOptions>()
            .Bind(configuration.GetSection(TenantWrightInboxOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton<TenantWrightOutboxDispatchSignal>();
        services.TryAddSingleton<ITenantDatabaseTopology, TenantDatabaseTopology>();
        services.TryAddSingleton<TenantDatabaseFanOut>();
        services.Replace(ServiceDescriptor.Scoped<ITenantWrightMessageSender, TenantWrightOutboxMessageSender<TDbContext>>());
        services.Replace(ServiceDescriptor.Scoped<ITenantWrightInboxTransaction, TenantWrightInboxTransaction<TDbContext>>());
        services.AddTenantWrightProtectedReplayIdempotencyStore<TDbContext>();
        services.AddHostedService<TenantWrightOutboxDispatcher<TDbContext>>();
        services.AddHostedService<TenantWrightInboxRetentionService<TDbContext>>();
        return services;
    }
}
