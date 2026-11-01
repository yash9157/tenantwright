using System.Globalization;
using System.Text;
using MassTransit;
using MassTransit.Context;
using MassTransit.Middleware;
using MassTransit.Serialization;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.MassTransit;

public sealed class TenantWrightErrorTransportFilter(
    TenantWrightReceiveEndpoint endpoint,
    TenantWrightAttestationService attestations,
    TimeProvider timeProvider) : IFilter<ExceptionReceiveContext>
{
    private readonly ErrorTransportFilter _transport = new();

    public Task Send(ExceptionReceiveContext context, IPipe<ExceptionReceiveContext> next)
    {
        var terminal = TenantWrightTerminalContext.Create(context, endpoint, "fault", attestations, timeProvider);
        var proxy = new TenantWrightExceptionReceiveContextProxy(context, terminal.RabbitContext, terminal.Headers);
        return ((IFilter<ExceptionReceiveContext>)_transport).Send(proxy, next);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("tenantwrightErrorTransport");
}

public sealed class TenantWrightDeadLetterTransportFilter(
    TenantWrightReceiveEndpoint endpoint,
    TenantWrightAttestationService attestations,
    TimeProvider timeProvider) : IFilter<ReceiveContext>
{
    private readonly DeadLetterTransportFilter _transport = new();

    public Task Send(ReceiveContext context, IPipe<ReceiveContext> next)
    {
        var terminal = TenantWrightTerminalContext.Create(context, endpoint, "skipped", attestations, timeProvider);
        var proxy = new TenantWrightReceiveContextProxy(context, terminal.RabbitContext);
        return ((IFilter<ReceiveContext>)_transport).Send(proxy, next);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("tenantwrightDeadLetterTransport");
}

internal static class TenantWrightTerminalContext
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static TenantWrightTerminalizedContext Create(
        ReceiveContext context,
        TenantWrightReceiveEndpoint endpoint,
        string classification,
        TenantWrightAttestationService attestations,
        TimeProvider timeProvider)
    {
        if (!context.TryGetPayload<RabbitMqBasicConsumeContext>(out var rabbit))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var rawDigest = TenantWrightAttestationService.Sha256(context.Body.GetBytes());
        var originChainDigest = ChainDigest(rabbit.Properties.Headers, TenantWrightTrustHeaders.OriginSignature, required: true)!;
        var relayChainDigest = ChainDigest(rabbit.Properties.Headers, TenantWrightTrustHeaders.RelaySignature, required: false);
        var timestamp = timeProvider.GetUtcNow();
        var terminal = attestations.CreateTerminal(
            rawDigest,
            originChainDigest,
            relayChainDigest,
            endpoint.Name,
            NewId.NextGuid().ToString("D", CultureInfo.InvariantCulture),
            classification,
            timestamp);

        var terminalHeaders = CreateHeaders(terminal);
        var properties = new BasicProperties(rabbit.Properties)
        {
            UserId = endpoint.OwnerPrincipal,
            Headers = CopyHeaders(rabbit.Properties.Headers)
        };
        foreach (var header in TenantWrightTrustHeaders.Custody.Where(TenantWrightTrustHeaders.IsTerminalHeader))
            properties.Headers.Remove(header);
        foreach (var item in terminalHeaders)
            properties.Headers[item.Key] = item.Value;

        return new TenantWrightTerminalizedContext(
            new TenantWrightRabbitMqBasicConsumeContext(rabbit, properties),
            terminalHeaders);
    }

    private static Dictionary<string, object?> CreateHeaders(TerminalAttestation terminal) => new(StringComparer.Ordinal)
    {
        [TenantWrightTrustHeaders.TerminalRawMessageSha256] = terminal.RawMessageSha256,
        [TenantWrightTrustHeaders.TerminalOriginChainSha256] = terminal.OriginChainSha256,
        [TenantWrightTrustHeaders.TerminalRelayChainSha256] = terminal.RelayChainSha256 ?? string.Empty,
        [TenantWrightTrustHeaders.TerminalId] = terminal.TerminalId,
        [TenantWrightTrustHeaders.TerminalClassification] = terminal.Classification,
        [TenantWrightTrustHeaders.TerminalTimestamp] = terminal.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        [TenantWrightTrustHeaders.TerminalKeyId] = terminal.KeyId,
        [TenantWrightTrustHeaders.TerminalSignature] = terminal.Signature
    };

    private static Dictionary<string, object?> CopyHeaders(IDictionary<string, object?>? source)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source is null)
            return result;
        foreach (var item in source)
            result.Add(item.Key, item.Value);
        return result;
    }

    private static string? ChainDigest(IDictionary<string, object?>? headers, string key, bool required)
    {
        if (headers is null || !headers.TryGetValue(key, out var raw))
            return required ? TenantWrightAttestationService.Sha256(Utf8.GetBytes($"missing:{key}")) : null;

        var value = string.Empty;
        try
        {
            value = raw switch
            {
                string text => text,
                byte[] bytes => Utf8.GetString(bytes),
                ReadOnlyMemory<byte> bytes => Utf8.GetString(bytes.Span),
                null => "unsupported:null",
                _ => $"unsupported:{raw.GetType().FullName}"
            };
            return TenantWrightAttestationService.SignatureDigest(value);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return TenantWrightAttestationService.Sha256(Utf8.GetBytes($"invalid:{value}"));
        }
    }
}

internal sealed record TenantWrightTerminalizedContext(
    RabbitMqBasicConsumeContext RabbitContext,
    IReadOnlyDictionary<string, object?> Headers);

internal sealed class TenantWrightRabbitMqBasicConsumeContext(
    RabbitMqBasicConsumeContext context,
    IReadOnlyBasicProperties properties) : RabbitMqBasicConsumeContext
{
    public string? RoutingKey => context.RoutingKey;
    public string Exchange => context.Exchange;
    public string ConsumerTag => context.ConsumerTag;
    public ulong DeliveryTag => context.DeliveryTag;
    public IReadOnlyBasicProperties Properties { get; } = properties;
}

internal sealed class TenantWrightReceiveContextProxy(
    ReceiveContext context,
    RabbitMqBasicConsumeContext rabbitContext) : ReceiveContextProxy(context)
{
    public override bool HasPayloadType(Type contextType) =>
        contextType == typeof(RabbitMqBasicConsumeContext) || base.HasPayloadType(contextType);

    public override bool TryGetPayload<TPayload>(out TPayload payload)
    {
        if (typeof(TPayload) == typeof(RabbitMqBasicConsumeContext))
        {
            payload = (TPayload)rabbitContext;
            return true;
        }
        return base.TryGetPayload(out payload!);
    }
}

internal sealed class TenantWrightExceptionReceiveContextProxy : ReceiveContextProxy, ExceptionReceiveContext
{
    private readonly ExceptionReceiveContext _context;
    private readonly RabbitMqBasicConsumeContext _rabbitContext;
    private readonly DictionarySendHeaders _headers = new();

    public TenantWrightExceptionReceiveContextProxy(
        ExceptionReceiveContext context,
        RabbitMqBasicConsumeContext rabbitContext,
        IReadOnlyDictionary<string, object?> headers) : base(context)
    {
        _context = context;
        _rabbitContext = rabbitContext;
        foreach (var item in headers)
            _headers.Set(item.Key, item.Value);
    }

    public Exception Exception => _context.Exception;
    public DateTime ExceptionTimestamp => _context.ExceptionTimestamp;
    public ExceptionInfo ExceptionInfo => _context.ExceptionInfo;
    public SendHeaders ExceptionHeaders => _headers;

    public override bool HasPayloadType(Type contextType) =>
        contextType == typeof(RabbitMqBasicConsumeContext) || base.HasPayloadType(contextType);

    public override bool TryGetPayload<TPayload>(out TPayload payload)
    {
        if (typeof(TPayload) == typeof(RabbitMqBasicConsumeContext))
        {
            payload = (TPayload)_rabbitContext;
            return true;
        }
        return base.TryGetPayload(out payload!);
    }
}
