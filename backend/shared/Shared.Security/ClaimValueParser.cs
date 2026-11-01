namespace Shared.Security;

public static class ClaimValueParser
{
    public static Guid? ToGuidOrNull(string? value)
    {
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    public static bool ToBooleanOrDefault(string? value, bool defaultValue = false)
    {
        if (bool.TryParse(value, out var boolean))
        {
            return boolean;
        }

        return defaultValue;
    }

    public static bool TryParseCanonicalGuidClaims(
        IEnumerable<string?> values,
        int maximumCount,
        out List<Guid> identifiers)
    {
        var materialized = values.ToArray();
        identifiers = [];
        if (maximumCount <= 0 || materialized.Length > maximumCount)
        {
            return false;
        }

        foreach (var value in materialized)
        {
            if (!Guid.TryParseExact(value, "D", out var identifier) || identifier == Guid.Empty)
            {
                identifiers = [];
                return false;
            }

            identifiers.Add(identifier);
        }

        identifiers = identifiers.Distinct().OrderBy(x => x).ToList();
        return true;
    }
}
