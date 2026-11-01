namespace Notifications.Domain.Policy;

/// <summary>Validates declared IANA identifiers and resolves DST-safe local boundaries.</summary>
public static class IanaTimeZoneRules
{
    public static string NormalizeAndValidate(string value)
    {
        var normalized = global::Shared.Check.NotNullOrWhiteSpace(value, nameof(value), maxLength: 128);
        if (!string.Equals(normalized, "UTC", StringComparison.OrdinalIgnoreCase) &&
            !TimeZoneInfo.TryConvertIanaIdToWindowsId(normalized, out _))
        {
            throw new ArgumentException("A valid IANA time-zone identifier is required.", nameof(value));
        }

        _ = Find(normalized);
        return normalized;
    }

    public static TimeZoneInfo Find(string ianaTimeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ArgumentException("A valid IANA time-zone identifier is required.", nameof(ianaTimeZoneId));
        }
        catch (InvalidTimeZoneException)
        {
            throw new ArgumentException("A valid IANA time-zone identifier is required.", nameof(ianaTimeZoneId));
        }
    }

    public static DateTime ConvertLocalBoundaryToUtc(
        DateTime localBoundary,
        TimeZoneInfo zone,
        bool preferLatestAmbiguousInstant)
    {
        var local = DateTime.SpecifyKind(localBoundary, DateTimeKind.Unspecified);
        for (var minute = 0; minute <= 180 && zone.IsInvalidTime(local); minute++)
        {
            if (minute == 180)
            {
                throw new InvalidOperationException("The local time-zone boundary remained invalid beyond the supported DST transition window.");
            }

            local = local.AddMinutes(1);
        }

        if (!zone.IsAmbiguousTime(local))
        {
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }

        var candidates = zone.GetAmbiguousTimeOffsets(local)
            .Select(offset => new DateTimeOffset(local, offset).UtcDateTime)
            .ToArray();
        return preferLatestAmbiguousInstant ? candidates.Max() : candidates.Min();
    }
}
