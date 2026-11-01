using System.Text.Json;
using Shared.AuditLogs.Auditing;
using Shared.EventBus.Messaging;

namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>
/// One stamped distributed message waiting for, or kept briefly after, broker-confirmed dispatch. Written in the same
/// transaction as the producer's business changes; claimed and dispatched by <c>TenantWrightOutboxDispatcher</c>.
/// </summary>
/// <remarks>
/// Transport infrastructure, not a business entity: it has no tenancy filter, soft delete or entity history, and its
/// payload never reaches audit logs. <see cref="TenantId"/> is an operational reference only; the signed origin header
/// is the tenant trust source. <see cref="LastErrorType"/> holds an exception type name, never a message that could
/// carry payload data.
/// </remarks>
[DisableAuditing]
public sealed class MessagingOutboxMessage
{
    public Guid Id { get; private set; }
    public string ContractIdentity { get; private set; } = string.Empty;
    public TenantWrightMessageRouteKind Kind { get; private set; }
    public string? Destination { get; private set; }
    public byte[] Payload { get; private set; } = [];
    public string Headers { get; private set; } = "{}";
    public Guid? TenantId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset AvailableAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public string? LockedBy { get; private set; }
    public DateTimeOffset? DispatchedAtUtc { get; private set; }
    public DateTimeOffset? ParkedAtUtc { get; private set; }
    public string? LastErrorType { get; private set; }

    private MessagingOutboxMessage()
    {
    }

    public static MessagingOutboxMessage Create(TenantWrightOutboundMessage message, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new MessagingOutboxMessage
        {
            Id = message.MessageId,
            ContractIdentity = message.ContractIdentity,
            Kind = message.Kind,
            Destination = message.Destination,
            Payload = message.Payload,
            Headers = JsonSerializer.Serialize(message.Headers),
            TenantId = message.TenantId,
            CreatedAtUtc = createdAtUtc,
            AvailableAtUtc = createdAtUtc,
            Attempts = 0
        };
    }
}

/// <summary>
/// Records that one endpoint has processed one message. Inserted in the consuming transaction, so a duplicate
/// delivery finds it and is acknowledged without running the handler again.
/// </summary>
[DisableAuditing]
public sealed class MessagingInboxRecord
{
    public Guid MessageId { get; private set; }
    public string Endpoint { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAtUtc { get; private set; }

    private MessagingInboxRecord()
    {
    }
}
