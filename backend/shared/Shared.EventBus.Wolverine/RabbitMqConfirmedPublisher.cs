using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// The provider's only outbound path: one RabbitMQ connection and one channel with publisher confirmations and
/// confirmation tracking, publishing every message as mandatory. A publish completes only once the broker has
/// confirmed it; a nack, a <c>basic.return</c> (unroutable), a closed channel or a cancelled wait surfaces as an
/// exception, so the outbox never marks an unconfirmed or unrouted message dispatched.
/// </summary>
/// <remarks>
/// Wolverine's own RabbitMQ sender is not used: it publishes to an exchange without the mandatory flag, so a message
/// to an exchange with no binding would be confirmed and silently dropped, and it cannot stamp the AMQP
/// <c>user-id</c> that binds the broker identity to the signed origin. Publishes are serialized on one channel, which
/// matches the single outbox dispatcher per host. The connection does not recover automatically: a failed publish
/// discards its channel, a closed connection is replaced, and the next dispatch, retried by the outbox, opens fresh
/// ones.
/// </remarks>
internal sealed class RabbitMqConfirmedPublisher(IOptions<MessagingOptions> options) : IAsyncDisposable
{
    private static readonly CreateChannelOptions ConfirmedChannel = new(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionFactory? _factory;
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    /// <summary>Publishes one message as mandatory and returns once the broker confirmed it accepted and routed it.</summary>
    public async Task PublishAsync(
        string exchange,
        BasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentNullException.ThrowIfNull(properties);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await OpenChannelAsync(cancellationToken);
            try
            {
                await channel.BasicPublishAsync(exchange, string.Empty, mandatory: true, properties, body, cancellationToken);
            }
            catch (PublishException) when (channel.IsOpen)
            {
                // A nack or return is a per-message outcome; the channel and its confirmation tracking stay usable.
                throw;
            }
            catch
            {
                // Closed by the broker (for example an absent exchange or a rejected user-id) or abandoned mid-confirm:
                // the channel's confirmation state can no longer be trusted. A closed connection is replaced on the
                // next open.
                await DisposeChannelAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens the publisher connection when it is not already open. Used by the readiness check.</summary>
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await OpenConnectionAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await DiscardAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<IChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await DisposeChannelAsync();
        var connection = await OpenConnectionAsync(cancellationToken);
        _channel = await connection.CreateChannelAsync(ConfirmedChannel, cancellationToken);
        return _channel;
    }

    private async Task<IConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is { IsOpen: true })
            return _connection;

        await DiscardAsync();
        _factory ??= RabbitMqConnectionFactories.Create(options.Value, automaticRecovery: false);
        _connection = await _factory.CreateConnectionAsync(cancellationToken);
        return _connection;
    }

    private async Task DiscardAsync()
    {
        await DisposeChannelAsync();
        if (_connection is { } connection)
        {
            _connection = null;
            await connection.DisposeAsync();
        }
    }

    private async Task DisposeChannelAsync()
    {
        if (_channel is { } channel)
        {
            _channel = null;
            await channel.DisposeAsync();
        }
    }
}
