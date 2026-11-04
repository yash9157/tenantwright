using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Shared.EventBus.Wolverine.Tests;

/// <summary>
/// A plain RabbitMQ.Client connection under one broker user, used to act as an attacker or an operator would: publish
/// arbitrary properties and bodies, declare objects, count and read queues. Publishing always uses publisher confirms.
/// </summary>
public sealed class RawBrokerClient(string hostName, int port, string virtualHost, string user, string password)
    : IAsyncDisposable
{
    private IConnection? _connection;

    public async Task PublishAsync(
        string exchange,
        BasicProperties properties,
        ReadOnlyMemory<byte> body,
        bool mandatory = true)
    {
        await using var channel = await (await ConnectAsync()).CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await channel.BasicPublishAsync(exchange, string.Empty, mandatory, properties, body);
    }

    /// <summary>Ready messages in <paramref name="queue"/>; <see langword="null"/> when the queue does not exist.</summary>
    public async Task<uint?> ReadyCountAsync(string queue)
    {
        await using var channel = await (await ConnectAsync()).CreateChannelAsync();
        try
        {
            return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
        }
        catch (OperationInterruptedException)
        {
            return null;
        }
    }

    /// <summary>Removes and returns the next ready message, or <see langword="null"/> when there is none.</summary>
    public async Task<BasicGetResult?> TakeAsync(string queue)
    {
        await using var channel = await (await ConnectAsync()).CreateChannelAsync();
        return await channel.BasicGetAsync(queue, autoAck: true);
    }

    public async Task DeclareFanoutExchangeAsync(string exchange)
    {
        await using var channel = await (await ConnectAsync()).CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
    }

    public async Task DeleteExchangeAsync(string exchange)
    {
        await using var channel = await (await ConnectAsync()).CreateChannelAsync();
        await channel.ExchangeDeleteAsync(exchange);
    }

    public async Task BindAsync(string queue, string exchange)
    {
        await using var channel = await (await ConnectAsync()).CreateChannelAsync();
        await channel.QueueBindAsync(queue, exchange, string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private async Task<IConnection> ConnectAsync()
    {
        if (_connection is { IsOpen: true })
            return _connection;
        if (_connection is not null)
            await _connection.DisposeAsync();

        var factory = new ConnectionFactory
        {
            HostName = hostName,
            Port = port,
            VirtualHost = virtualHost,
            UserName = user,
            Password = password,
            ClientProvidedName = $"wolverine-it-{user}",
            AutomaticRecoveryEnabled = false
        };
        _connection = await factory.CreateConnectionAsync();
        return _connection;
    }
}
