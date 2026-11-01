using System.Globalization;
using System.Text.Json;
using Notifications.Application.Contracts.Delivery;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery.Providers;

/// <summary>
/// Provider-agnostic JSON email-event parser for the foundation release. Accepts either a single
/// event object or an array of events, each with (case-insensitive) fields:
/// <c>id</c>/<c>eventId</c> (provider event id), <c>messageId</c> (provider message id),
/// <c>type</c>/<c>event</c> (delivered/bounce/complaint), optional <c>code</c>, <c>reason</c>, and
/// <c>timestamp</c> (unix seconds or ISO-8601). Events without a recognized type, event id, or
/// message id are skipped. A provider whose payload differs materially gets its own parser.
/// </summary>
public sealed class GenericJsonEmailEventParser : IEmailEventParser, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<EmailDeliveryEvent> Parse(EmailWebhookRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RawBody))
        {
            return [];
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(request.RawBody, JsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        var results = new List<EmailDeliveryEvent>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                AddIfValid(element, results);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            AddIfValid(root, results);
        }

        return results;
    }

    private static void AddIfValid(JsonElement element, List<EmailDeliveryEvent> results)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var providerEventId = GetString(element, "id") ?? GetString(element, "eventId");
        var providerMessageId = GetString(element, "messageId") ?? GetString(element, "message_id");
        var type = MapType(GetString(element, "type") ?? GetString(element, "event"));

        if (string.IsNullOrWhiteSpace(providerEventId) ||
            string.IsNullOrWhiteSpace(providerMessageId) ||
            type is null)
        {
            return;
        }

        results.Add(new EmailDeliveryEvent
        {
            ProviderEventId = providerEventId!,
            ProviderMessageId = providerMessageId!,
            EventType = type.Value,
            ProviderCode = GetString(element, "code"),
            SafeReason = GetString(element, "reason"),
            OccurredAtUtc = ParseTimestamp(GetString(element, "timestamp"))
        });
    }

    private static EmailDeliveryEventType? MapType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "delivered" or "delivery" => EmailDeliveryEventType.Delivered,
            "bounce" or "hard_bounce" or "soft_bounce" or "bounced" => EmailDeliveryEventType.Bounce,
            "complaint" or "complained" or "spam" or "spamreport" => EmailDeliveryEventType.Complaint,
            _ => null
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.ToString(),
                    _ => null
                };
            }
        }

        return null;
    }

    private static DateTime? ParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return null;
        }

        if (long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }

        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        return null;
    }
}
