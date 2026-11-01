using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shared.EventContracts;
using Shared.Events;

namespace Shared.EventBus.Messaging;

public enum TenantWrightMessageRouteKind
{
    Command = 1,
    Event = 2
}

public sealed record TenantWrightContractRoute(
    Type ContractType,
    TenantWrightMessageRouteKind Kind,
    string? CommandDestination,
    IReadOnlySet<string> AllowedOriginPrincipals,
    JsonSerializerContext? SerializationContext = null);

public sealed record TenantWrightEndpointRoute(
    string Name,
    string OwnerPrincipal,
    IReadOnlyList<Type> ContractTypes);

/// <summary>
/// A receive endpoint this host consumes. Transport-neutral: every contract type is handled by the
/// <see cref="ITenantWrightMessageHandler{TMessage}"/> registered for it, and the transport provider owns how the
/// endpoint is materialized.
/// </summary>
public sealed record TenantWrightReceiveEndpoint(
    string Name,
    string OwnerPrincipal,
    IReadOnlyList<Type> ContractTypes);

public sealed class TenantWrightMessageCatalog
{
    private readonly FrozenDictionary<Type, TenantWrightContractRoute> _routes;
    private readonly FrozenDictionary<string, TenantWrightContractRoute> _routesByIdentity;
    private readonly FrozenDictionary<string, TenantWrightEndpointRoute> _endpointRoutes;
    private readonly FrozenDictionary<string, TenantWrightReceiveEndpoint> _receiveEndpoints;
    private readonly ImmutableArray<TenantWrightContractRoute> _routeList;
    private readonly ImmutableArray<TenantWrightEndpointRoute> _endpointRouteList;
    private readonly ImmutableArray<TenantWrightReceiveEndpoint> _receiveEndpointList;

    public TenantWrightMessageCatalog(
        IEnumerable<TenantWrightContractRoute> routes,
        IEnumerable<TenantWrightEndpointRoute> endpointRoutes,
        IEnumerable<TenantWrightReceiveEndpoint> receiveEndpoints)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(endpointRoutes);
        ArgumentNullException.ThrowIfNull(receiveEndpoints);

        _routeList = routes.Select(CopyRoute).ToImmutableArray();
        _endpointRouteList = endpointRoutes.Select(CopyEndpointRoute).ToImmutableArray();
        _receiveEndpointList = receiveEndpoints.Select(CopyReceiveEndpoint).ToImmutableArray();
        _routes = _routeList.ToFrozenDictionary(route => route.ContractType);
        _endpointRoutes = _endpointRouteList.ToFrozenDictionary(endpoint => endpoint.Name, StringComparer.Ordinal);
        _receiveEndpoints = _receiveEndpointList.ToFrozenDictionary(endpoint => endpoint.Name, StringComparer.Ordinal);
        Validate();

        // Validation has proven every route carries exactly one EventName equal to its full type name.
        _routesByIdentity = _routeList.ToFrozenDictionary(
            route => TenantWrightMessageContracts.GetIdentity(route.ContractType),
            StringComparer.Ordinal);
    }

    public IReadOnlyCollection<TenantWrightContractRoute> Routes => _routeList;
    public IReadOnlyCollection<TenantWrightEndpointRoute> EndpointRoutes => _endpointRouteList;
    public IReadOnlyCollection<TenantWrightReceiveEndpoint> ReceiveEndpoints => _receiveEndpointList;

    public IReadOnlyCollection<JsonSerializerContext> SerializationContexts => _routeList
        .Where(route => route.SerializationContext is not null)
        .Select(route => route.SerializationContext!).Distinct().ToArray();

    public byte[] SerializeCanonical(object message, Type contractType)
    {
        var route = GetRoute(contractType);
        var metadata = (route.SerializationContext ?? TenantWrightContractJsonContext.Default).GetTypeInfo(contractType)
            ?? throw new InvalidOperationException("The approved distributed contract requires explicit owner-generated JSON metadata.");
        return TenantWrightContractSerializer.SerializeCanonical(message, metadata);
    }

    /// <summary>
    /// Deserializes a canonical payload into its exact approved contract type with the owner-generated metadata the
    /// canonical serializer uses, so a round trip reproduces the signed payload digest.
    /// </summary>
    public object DeserializeCanonical(ReadOnlySpan<byte> payload, Type contractType)
    {
        var route = GetRoute(contractType);
        var metadata = (route.SerializationContext ?? TenantWrightContractJsonContext.Default).GetTypeInfo(contractType)
            ?? throw new InvalidOperationException("The approved distributed contract requires explicit owner-generated JSON metadata.");
        return JsonSerializer.Deserialize(payload, metadata)
            ?? throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
    }

    public TenantWrightContractRoute GetRoute(Type contractType) =>
        _routes.TryGetValue(contractType, out var route)
            ? route
            : throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);

    /// <summary>The approved route whose canonical <c>EventName</c> identity is <paramref name="contractIdentity"/>.</summary>
    public TenantWrightContractRoute GetRouteByIdentity(string contractIdentity) =>
        _routesByIdentity.TryGetValue(contractIdentity, out var route)
            ? route
            : throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);

    public bool TryGetRoute(Type contractType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TenantWrightContractRoute? route) =>
        _routes.TryGetValue(contractType, out route);

    public TenantWrightEndpointRoute GetEndpointRoute(string endpointName) =>
        _endpointRoutes.TryGetValue(endpointName, out var endpoint)
            ? endpoint
            : throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

    public TenantWrightReceiveEndpoint GetReceiveEndpoint(string endpointName) =>
        _receiveEndpoints.TryGetValue(endpointName, out var endpoint)
            ? endpoint
            : throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

    private static TenantWrightContractRoute CopyRoute(TenantWrightContractRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(route.ContractType);
        ArgumentNullException.ThrowIfNull(route.AllowedOriginPrincipals);
        return route with
        {
            AllowedOriginPrincipals = route.AllowedOriginPrincipals.ToFrozenSet(StringComparer.Ordinal)
        };
    }

    private static TenantWrightEndpointRoute CopyEndpointRoute(TenantWrightEndpointRoute endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpoint.ContractTypes);
        return endpoint with { ContractTypes = endpoint.ContractTypes.ToImmutableArray() };
    }

    private static TenantWrightReceiveEndpoint CopyReceiveEndpoint(TenantWrightReceiveEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpoint.ContractTypes);
        return endpoint with { ContractTypes = endpoint.ContractTypes.ToImmutableArray() };
    }

    private void Validate()
    {
        if (_routes.Count == 0)
            throw new InvalidOperationException("At least one explicit TenantWright message route is required.");

        foreach (var route in _routeList)
        {
            if (route.SerializationContext is { } serializer &&
                (serializer.GetTypeInfo(route.ContractType)?.Type != route.ContractType ||
                 serializer.Options.PropertyNamingPolicy != JsonNamingPolicy.CamelCase ||
                 serializer.Options.PropertyNameCaseInsensitive || serializer.Options.WriteIndented))
                throw new InvalidOperationException("Owner serialization must provide exact camel-case contract metadata with canonical options.");
            var metadata = route.ContractType.GetCustomAttributes(typeof(DistributedContractAttribute), false)
                .Cast<DistributedContractAttribute>().SingleOrDefault()
                ?? throw new InvalidOperationException($"Route contract '{route.ContractType.FullName}' is missing exact distributed contract metadata.");
            var eventName = route.ContractType.GetCustomAttributes(typeof(EventNameAttribute), false)
                .Cast<EventNameAttribute>().SingleOrDefault();
            if (eventName is null || !string.Equals(eventName.Name, route.ContractType.FullName, StringComparison.Ordinal))
                throw new InvalidOperationException("Every route must preserve its canonical EventName identity.");
            if (metadata.SchemaVersion <= 0 || route.AllowedOriginPrincipals.Count == 0 ||
                route.AllowedOriginPrincipals.Any(actor => !TenantWrightName.IsPrincipal(actor) ||
                    string.Equals(actor, TenantWrightName.ReplayPrincipal, StringComparison.Ordinal)))
                throw new InvalidOperationException("Every route requires immutable schema metadata and exact non-replay TenantWright origin principals.");
            if (route.Kind == TenantWrightMessageRouteKind.Command && !TenantWrightName.IsEndpoint(route.CommandDestination))
                throw new InvalidOperationException("Every command requires one explicit tenantwright-* destination.");
            if (route.Kind == TenantWrightMessageRouteKind.Event && route.CommandDestination is not null)
                throw new InvalidOperationException("Published events cannot define a command destination.");
            if (route.Kind is not (TenantWrightMessageRouteKind.Command or TenantWrightMessageRouteKind.Event))
                throw new InvalidOperationException("Every route must have one approved route kind.");
        }

        foreach (var endpoint in _endpointRouteList)
            ValidateEndpoint(endpoint.Name, endpoint.OwnerPrincipal, endpoint.ContractTypes);

        foreach (var endpoint in _receiveEndpointList)
        {
            ValidateEndpoint(endpoint.Name, endpoint.OwnerPrincipal, endpoint.ContractTypes);
            if (!_endpointRoutes.TryGetValue(endpoint.Name, out var declared) ||
                !string.Equals(declared.OwnerPrincipal, endpoint.OwnerPrincipal, StringComparison.Ordinal) ||
                !declared.ContractTypes.SequenceEqual(endpoint.ContractTypes))
                throw new InvalidOperationException("Every local receive endpoint must exactly match one declared endpoint route.");
        }

        foreach (var route in _routeList.Where(route => route.Kind == TenantWrightMessageRouteKind.Command))
        {
            var consumers = _endpointRouteList.Where(endpoint => endpoint.ContractTypes.Contains(route.ContractType)).ToArray();
            if (consumers.Length != 1 || !string.Equals(consumers[0].Name, route.CommandDestination, StringComparison.Ordinal))
                throw new InvalidOperationException("Every command destination must be the one declared endpoint route containing that exact contract type.");
        }
    }

    private void ValidateEndpoint(string name, string ownerPrincipal, IReadOnlyList<Type> contractTypes)
    {
        if (!TenantWrightName.IsEndpoint(name) || !TenantWrightName.IsPrincipal(ownerPrincipal) || contractTypes.Count == 0)
            throw new InvalidOperationException("Endpoints require exact kebab-case tenantwright-* names, one exact owner, and explicit bindings.");
        if (contractTypes.Distinct().Count() != contractTypes.Count || contractTypes.Any(type => !_routes.ContainsKey(type)))
            throw new InvalidOperationException("Endpoint bindings must be unique exact types present in the route catalog.");
    }
}

internal static class TenantWrightName
{
    public const string ReplayPrincipal = "tenantwright-messaging-replay";

    public static bool IsPrincipal(string? value) => IsKebab(value, "tenantwright-");
    public static bool IsEndpoint(string? value) => IsKebab(value, "tenantwright-");

    private static bool IsKebab(string? value, string prefix) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length <= 120 && value[^1] != '-' && !value.Contains("--", StringComparison.Ordinal) &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
