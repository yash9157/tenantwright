using System.Text.Json;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.Account.Risk;

namespace Identity.Application.SecurityLogs;

internal sealed class IdentitySecurityLogRiskSnapshot
{
    public string? RiskLevel { get; init; }
    public string? RiskAction { get; init; }
    public int? RiskScore { get; init; }
    public List<string> RiskReasons { get; init; } = [];
    public IdentityLoginRiskReasonFlags? RiskReasonFlags { get; init; }
    public bool? MfaEnrolled { get; init; }
    public bool? BlockedByRisk { get; init; }
    public bool? FalsePositive { get; init; }
    public string? CountryCode { get; init; }
    public Guid? RiskOverrideId { get; init; }
}

internal static class IdentitySecurityLogRiskMetadata
{
    private static readonly IdentitySecurityLogRiskSnapshot EmptySnapshot = new();

    public static IdentitySecurityLogRiskSnapshot Read(IdentitySecurityLog log)
    {
        var parsed = Parse(log.ExtraProperties);

        return new IdentitySecurityLogRiskSnapshot
        {
            RiskLevel = log.RiskLevel ?? parsed.RiskLevel,
            RiskAction = log.RiskAction ?? parsed.RiskAction,
            RiskScore = log.RiskScore ?? parsed.RiskScore,
            RiskReasons = parsed.RiskReasons,
            RiskReasonFlags = log.RiskReasonFlags.HasValue
                ? (IdentityLoginRiskReasonFlags)log.RiskReasonFlags.Value
                : parsed.RiskReasonFlags,
            MfaEnrolled = log.MfaEnrolled ?? parsed.MfaEnrolled,
            BlockedByRisk = log.BlockedByRisk ?? parsed.BlockedByRisk,
            FalsePositive = log.FalsePositive ?? parsed.FalsePositive,
            CountryCode = log.CountryCode ?? parsed.CountryCode,
            RiskOverrideId = log.RiskOverrideId ?? parsed.RiskOverrideId
        };
    }

    public static void ApplyScalarFields(IdentitySecurityLog log)
    {
        var parsed = Parse(log.ExtraProperties);
        log.RiskLevel = parsed.RiskLevel;
        log.RiskAction = parsed.RiskAction;
        log.RiskScore = parsed.RiskScore;
        log.RiskReasonFlags = parsed.RiskReasonFlags.HasValue
            ? (int)parsed.RiskReasonFlags.Value
            : null;
        log.MfaEnrolled = parsed.MfaEnrolled;
        log.BlockedByRisk = parsed.BlockedByRisk;
        log.FalsePositive = parsed.FalsePositive;
        log.CountryCode = parsed.CountryCode;
        log.RiskOverrideId = parsed.RiskOverrideId;
    }

    public static Dictionary<string, object?> ReadDictionary(string? extraPropertiesJson)
    {
        if (string.IsNullOrWhiteSpace(extraPropertiesJson))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(extraPropertiesJson);
            return parsed ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static IdentitySecurityLogRiskSnapshot Parse(string? extraPropertiesJson)
    {
        if (string.IsNullOrWhiteSpace(extraPropertiesJson))
        {
            return EmptySnapshot;
        }

        try
        {
            using var document = JsonDocument.Parse(extraPropertiesJson);
            var root = document.RootElement;
            var reasons = TryGetStringArray(root, "riskReasons");
            var riskScore = TryGetInt32(root, "riskScore");

            return new IdentitySecurityLogRiskSnapshot
            {
                RiskLevel = TryGetString(root, "riskLevel"),
                RiskAction = TryGetString(root, "riskAction"),
                RiskScore = riskScore,
                RiskReasons = reasons,
                RiskReasonFlags = riskScore.HasValue ? ToFlags(reasons) : null,
                MfaEnrolled = TryGetBoolean(root, "mfaEnrolled"),
                BlockedByRisk = TryGetBoolean(root, "blockedByRisk"),
                FalsePositive = TryGetBoolean(root, "falsePositive"),
                CountryCode = TryGetString(root, "countryCode"),
                RiskOverrideId = TryGetGuid(root, "riskOverrideId")
            };
        }
        catch (JsonException)
        {
            return EmptySnapshot;
        }
    }

    internal static IdentityLoginRiskReasonFlags ToFlags(IEnumerable<string> reasons)
    {
        var flags = IdentityLoginRiskReasonFlags.None;
        foreach (var reason in reasons)
        {
            flags |= reason switch
            {
                "new_device" => IdentityLoginRiskReasonFlags.NewDevice,
                "new_ip" => IdentityLoginRiskReasonFlags.NewIp,
                "geo_shift" => IdentityLoginRiskReasonFlags.GeoShift,
                "failure_burst" => IdentityLoginRiskReasonFlags.FailureBurst,
                "override:allow" => IdentityLoginRiskReasonFlags.OverrideAllow,
                "override:forcemfa" => IdentityLoginRiskReasonFlags.OverrideForceMfa,
                "override:block" => IdentityLoginRiskReasonFlags.OverrideBlock,
                _ => IdentityLoginRiskReasonFlags.Unknown
            };
        }

        return flags;
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static int? TryGetInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsedNumber))
        {
            return parsedNumber;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsedString)
            ? parsedString
            : null;
    }

    private static bool? TryGetBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsedString)
            ? parsedString
            : null;
    }

    private static Guid? TryGetGuid(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               Guid.TryParse(value.GetString(), out var guid)
            ? guid
            : null;
    }

    private static List<string> TryGetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToList();
    }
}
