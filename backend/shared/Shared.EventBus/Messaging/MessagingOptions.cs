namespace Shared.EventBus.Messaging;

/// <summary>The transport provider a deployment runs. Exactly one is active per host.</summary>
public enum TenantWrightMessagingProvider
{
    /// <summary>
    /// Bus-free: no transport, no outbox dispatcher and no consumers. Events are dropped, commands fail closed and
    /// audit records are written to the structured log instead of the durable audit pipeline.
    /// </summary>
    None = 0,

    /// <summary>MassTransit 9 over RabbitMQ. Commercial: requires an <c>MT_LICENSE</c> or <c>MT_LICENSE_PATH</c> source.</summary>
    MassTransit = 1,

    /// <summary>
    /// Wolverine 6 over RabbitMQ (MIT, no licence). Consumption only: immediate in-process retries and native
    /// dead-lettering, but no broker delayed redelivery, signed terminal custody or protected replay.
    /// </summary>
    Wolverine = 2
}

/// <summary>
/// Provider-neutral messaging configuration, bound from <c>Messaging</c>. The connection, delivery, trust and replay
/// settings apply to whichever <see cref="Provider"/> is active; <c>Messaging:Outbox</c> and <c>Messaging:Inbox</c>
/// configure the durable persistence and settings only one provider reads live under <c>Messaging:{Provider}</c>.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public TenantWrightMessagingProvider Provider { get; set; } = TenantWrightMessagingProvider.None;
    public string ServiceName { get; set; } = string.Empty;
    public string ProducerPrincipal { get; set; } = string.Empty;
    public MessagingRabbitMqOptions RabbitMq { get; set; } = new();
    public MessagingDeliveryOptions Delivery { get; set; } = new();
    public MessagingTrustOptions Trust { get; set; } = new();
    public MessagingReplayOptions Replay { get; set; } = new();
}

/// <summary>The RabbitMQ connection every RabbitMQ-backed provider uses. Not specific to one provider.</summary>
public sealed class MessagingRabbitMqOptions
{
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 5671;
    public string VirtualHost { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseTls { get; set; } = true;
    public string TrustedCaCertificatePath { get; set; } = string.Empty;
    public string ClientCertificatePath { get; set; } = string.Empty;
    public string ClientPrivateKeyPath { get; set; } = string.Empty;
    public bool AllowInsecureTransport { get; set; }
    public bool PublisherConfirmation { get; set; } = true;
    public bool DetectUnroutableMessages { get; set; } = true;
    public ushort PrefetchCount { get; set; } = 32;
    public ushort ConcurrentMessageLimit { get; set; } = 16;
}

public sealed class MessagingDeliveryOptions
{
    public int[] ImmediateRetryMilliseconds { get; set; } = [250, 1000];
    public int[] DelayedRedeliverySeconds { get; set; } = [30, 120, 600];
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class MessagingTrustOptions
{
    public string ActiveKeyId { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public TimeSpan MaximumOriginAge { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan MaximumFutureClockSkew { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumRelayLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public List<MessagingPublicKeyOptions> PublicKeys { get; set; } = [];
}

public sealed class MessagingPublicKeyOptions
{
    public string KeyId { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string PublicKeyPath { get; set; } = string.Empty;
    public bool Revoked { get; set; }
}

public sealed class MessagingReplayOptions
{
    public string Actor { get; set; } = "tenantwright-messaging-replay";
    public TimeSpan MaximumAuthorizationLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan TerminalRetention { get; set; } = TimeSpan.FromDays(30);
    public int MaximumDeathRecords { get; set; } = 8;
}
