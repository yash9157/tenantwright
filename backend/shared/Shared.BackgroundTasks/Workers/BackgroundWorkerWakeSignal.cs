using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Shared.BackgroundTasks.Workers;

public interface IBackgroundWorkerWakeSignal
{
    Task SignalAsync(
        string key,
        BackgroundWorkerWakeOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<BackgroundWorkerWakeResult> WaitAsync(
        string key,
        BackgroundWorkerWakeOptions? options = null,
        CancellationToken cancellationToken = default);
}

public enum BackgroundWorkerWakeResult
{
    Signal = 0,
    SafetyPollTimeout = 1
}

public enum BackgroundWorkerWakeMode
{
    LocalOnly = 0,
    ValkeyPubSub = 1
}

public sealed class BackgroundWorkerWakeOptions
{
    public bool Enabled { get; set; } = true;
    public BackgroundWorkerWakeMode WakeupMode { get; set; } = BackgroundWorkerWakeMode.ValkeyPubSub;
    public TimeSpan SafetyPollTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public string? ValkeyChannel { get; set; }
}

public sealed class BackgroundWorkerWakeSignal : IBackgroundWorkerWakeSignal, IDisposable
{
    private static readonly BoundedChannelOptions ChannelOptions = new(1)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    };

    private readonly IConnectionMultiplexer? _connectionMultiplexer;
    private readonly ConcurrentDictionary<string, Channel<bool>> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _subscribedValkeyChannels = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public BackgroundWorkerWakeSignal(
        ILogger<BackgroundWorkerWakeSignal>? logger = null,
        IConnectionMultiplexer? connectionMultiplexer = null)
    {
        Logger = logger ?? NullLogger<BackgroundWorkerWakeSignal>.Instance;
        _connectionMultiplexer = connectionMultiplexer;
    }

    public ILogger<BackgroundWorkerWakeSignal> Logger { get; }

    public async Task SignalAsync(
        string key,
        BackgroundWorkerWakeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var normalizedOptions = Normalize(options);
        if (!normalizedOptions.Enabled)
        {
            return;
        }

        SignalLocal(key);

        if (normalizedOptions.WakeupMode != BackgroundWorkerWakeMode.ValkeyPubSub ||
            string.IsNullOrWhiteSpace(normalizedOptions.ValkeyChannel))
        {
            return;
        }

        var subscriber = await TrySubscribeAsync(normalizedOptions, cancellationToken).ConfigureAwait(false);
        if (subscriber == null)
        {
            return;
        }

        try
        {
            await subscriber
                .PublishAsync(RedisChannel.Literal(normalizedOptions.ValkeyChannel), key)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to publish background worker wake signal. Key={WakeupKey}, Channel={WakeupChannel}",
                key,
                normalizedOptions.ValkeyChannel);
        }
    }

    public async Task<BackgroundWorkerWakeResult> WaitAsync(
        string key,
        BackgroundWorkerWakeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedOptions = Normalize(options);
        if (string.IsNullOrWhiteSpace(key) || !normalizedOptions.Enabled)
        {
            await Task.Delay(normalizedOptions.SafetyPollTimeout, cancellationToken).ConfigureAwait(false);
            RecordWake(key, BackgroundWorkerWakeResult.SafetyPollTimeout);
            return BackgroundWorkerWakeResult.SafetyPollTimeout;
        }

        if (normalizedOptions.WakeupMode == BackgroundWorkerWakeMode.ValkeyPubSub &&
            !string.IsNullOrWhiteSpace(normalizedOptions.ValkeyChannel))
        {
            await TrySubscribeAsync(normalizedOptions, cancellationToken).ConfigureAwait(false);
        }

        var channel = GetOrCreateChannel(key);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(normalizedOptions.SafetyPollTimeout);

        try
        {
            await channel.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
            RecordWake(key, BackgroundWorkerWakeResult.Signal);
            return BackgroundWorkerWakeResult.Signal;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordWake(key, BackgroundWorkerWakeResult.SafetyPollTimeout);
            return BackgroundWorkerWakeResult.SafetyPollTimeout;
        }
    }

    private async Task<ISubscriber?> TrySubscribeAsync(
        BackgroundWorkerWakeOptions options,
        CancellationToken cancellationToken)
    {
        if (_connectionMultiplexer == null)
        {
            Logger.LogWarning(
                "Valkey wakeup mode is enabled, but IConnectionMultiplexer is unavailable. Falling back to local-only wake signals.");
            return null;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var subscriber = _connectionMultiplexer.GetSubscriber();
            var channelName = options.ValkeyChannel!;
            if (_subscribedValkeyChannels.TryAdd(channelName, 0))
            {
                try
                {
                    await subscriber
                        .SubscribeAsync(
                            RedisChannel.Literal(channelName),
                            (_, message) => SignalLocal(message.ToString()))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _subscribedValkeyChannels.TryRemove(channelName, out _);
                    Logger.LogWarning(
                        ex,
                        "Failed to subscribe to background worker wake channel {WakeupChannel}. Falling back to local-only wake signals.",
                        channelName);
                    return null;
                }
            }

            return subscriber;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private Channel<bool> GetOrCreateChannel(string key)
    {
        return _channels.GetOrAdd(key, static _ => Channel.CreateBounded<bool>(ChannelOptions));
    }

    private void SignalLocal(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var channel = GetOrCreateChannel(key);
        channel.Writer.TryWrite(true);
    }

    private static BackgroundWorkerWakeOptions Normalize(BackgroundWorkerWakeOptions? options)
    {
        options ??= new BackgroundWorkerWakeOptions();
        if (options.SafetyPollTimeout <= TimeSpan.Zero)
        {
            options.SafetyPollTimeout = TimeSpan.FromSeconds(60);
        }

        options.ValkeyChannel = string.IsNullOrWhiteSpace(options.ValkeyChannel)
            ? null
            : options.ValkeyChannel.Trim();
        return options;
    }

    private static void RecordWake(string? key, BackgroundWorkerWakeResult result)
    {
        BackgroundTasksTelemetry.RecordWorkerWake(
            key,
            result == BackgroundWorkerWakeResult.Signal ? "signal" : "safety_poll");
    }

    public void Dispose()
    {
        _startLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
