using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Publishing;

namespace Shared.AuditLogs.AspNetCore;

internal enum AuditPayloadRedactionOutcome
{
    Empty,
    Redacted,
    ParsedWithoutSensitiveValues,
    Unredactable
}

internal readonly record struct AuditPayloadRedactionResult(
    AuditPayloadRedactionOutcome Outcome,
    string? Payload,
    int RedactionHits);

internal static class AuditLogRedactor
{
    private const int MaxJsonDepth = 16;

    public static int RedactProperties(
        IDictionary<string, string?> properties,
        AuditRedactionOptions options)
    {
        var redactionHits = 0;
        foreach (var key in properties.Keys.ToArray())
        {
            if (!IsSensitiveKey(key, options))
            {
                var result = RedactPayload(properties[key], options);
                if (result.Outcome == AuditPayloadRedactionOutcome.Redacted)
                {
                    properties[key] = result.Payload;
                    redactionHits += result.RedactionHits;
                }

                continue;
            }

            properties[key] = options.Mask;
            redactionHits++;
        }

        return redactionHits;
    }

    public static bool RedactJsonPayload(
        string? payload,
        AuditRedactionOptions options,
        out string? redactedPayload,
        out int redactionHits)
    {
        var result = RedactJsonPayloadCore(payload, options);
        redactedPayload = result.Payload;
        redactionHits = result.RedactionHits;
        return result.Outcome == AuditPayloadRedactionOutcome.Redacted;
    }

    public static AuditPayloadRedactionResult RedactPayload(
        string? payload,
        AuditRedactionOptions options)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.Empty, payload, 0);
        }

        var trimmed = payload.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return RedactJsonPayloadCore(payload, options);
        }

        return RedactFormPayload(payload, options);
    }

    public static int RedactEntityChanges(
        IList<AuditLogWriteRequestEntityChange> entityChanges,
        AuditRedactionOptions options)
    {
        var redactionHits = 0;
        foreach (var entityChange in entityChanges)
        {
            foreach (var propertyChange in entityChange.PropertyChanges)
            {
                if (!IsSensitiveKey(propertyChange.PropertyName, options))
                {
                    continue;
                }

                propertyChange.OriginalValue = options.Mask;
                propertyChange.NewValue = options.Mask;
                redactionHits++;
            }
        }

        return redactionHits;
    }

    public static string? HashValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static bool IsSensitiveKey(string? key, AuditRedactionOptions options)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        for (var i = 0; i < options.SensitiveKeys.Count; i++)
        {
            if (key.Contains(options.SensitiveKeys[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static AuditPayloadRedactionResult RedactJsonPayloadCore(
        string? payload,
        AuditRedactionOptions options)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.Empty, payload, 0);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(
                payload,
                new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch
        {
            return new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.Unredactable, payload, 0);
        }

        if (node is null)
        {
            return new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.Unredactable, payload, 0);
        }

        var redactionHits = RedactJsonNode(node, options);
        if (redactionHits <= 0)
        {
            return new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.ParsedWithoutSensitiveValues, payload, 0);
        }

        return new AuditPayloadRedactionResult(
            AuditPayloadRedactionOutcome.Redacted,
            node.ToJsonString(new JsonSerializerOptions { MaxDepth = MaxJsonDepth }),
            redactionHits);
    }

    private static AuditPayloadRedactionResult RedactFormPayload(
        string payload,
        AuditRedactionOptions options)
    {
        if (!payload.Contains('=', StringComparison.Ordinal))
        {
            return new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.Unredactable, payload, 0);
        }

        var redactionHits = 0;
        var segments = payload.Split('&');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0)
            {
                continue;
            }

            var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.Unredactable, payload, 0);
            }

            var encodedKey = segment[..separatorIndex];
            var key = WebUtility.UrlDecode(encodedKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                return new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.Unredactable, payload, 0);
            }

            if (!IsSensitiveKey(key, options))
            {
                continue;
            }

            segments[index] = $"{encodedKey}={WebUtility.UrlEncode(options.Mask)}";
            redactionHits++;
        }

        return redactionHits > 0
            ? new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.Redacted, string.Join('&', segments), redactionHits)
            : new AuditPayloadRedactionResult(AuditPayloadRedactionOutcome.ParsedWithoutSensitiveValues, payload, 0);
    }

    private static int RedactJsonNode(JsonNode node, AuditRedactionOptions options)
    {
        if (node is JsonObject jsonObject)
        {
            var hits = 0;
            foreach (var key in jsonObject.Select(property => property.Key).ToArray())
            {
                if (IsSensitiveKey(key, options))
                {
                    jsonObject[key] = options.Mask;
                    hits++;
                    continue;
                }

                if (jsonObject[key] is { } child)
                {
                    hits += RedactJsonNode(child, options);
                }
            }

            return hits;
        }

        if (node is JsonArray jsonArray)
        {
            var hits = 0;
            foreach (var child in jsonArray)
            {
                if (child is not null)
                {
                    hits += RedactJsonNode(child, options);
                }
            }

            return hits;
        }

        return 0;
    }
}
