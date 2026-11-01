using Notifications.Domain.Policy;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Policy;

internal static class NotificationPolicyCalculator
{
    private static readonly string[] MandatoryBypassCategories = ["Security", "Urgent"];

    public static NotificationPolicyDecision Evaluate(
        NotificationChannel channel,
        string? category,
        NotificationPriority priority,
        DateTime atUtc,
        TenantNotificationPolicy? tenantPolicy,
        UserNotificationPreference? userPreference)
    {
        var utc = EnsureUtc(atUtc);
        if (channel is NotificationChannel.App or NotificationChannel.WhatsApp)
        {
            return NotificationPolicyDecision.Allow("channel_not_eligible");
        }

        if (priority is NotificationPriority.High or NotificationPriority.Critical)
        {
            return NotificationPolicyDecision.Bypass("priority_bypass");
        }

        if (IsBypassCategory(category, tenantPolicy))
        {
            return NotificationPolicyDecision.Bypass("category_bypass");
        }

        var channelFlag = ToPolicyChannel(channel);
        var windows = new List<QuietWindow>();
        if (tenantPolicy is { QuietHoursEnabled: true } && tenantPolicy.QuietChannels.HasFlag(channelFlag))
        {
            windows.Add(new QuietWindow(
                tenantPolicy.IanaTimeZoneId,
                tenantPolicy.QuietStartLocalMinutes,
                tenantPolicy.QuietEndLocalMinutes));
        }

        if (userPreference is { QuietHoursEnabled: true } && userPreference.QuietChannels.HasFlag(channelFlag))
        {
            windows.Add(new QuietWindow(
                userPreference.IanaTimeZoneId,
                userPreference.QuietStartLocalMinutes,
                userPreference.QuietEndLocalMinutes));
        }

        if (windows.Count == 0)
        {
            return NotificationPolicyDecision.Allow("quiet_hours_not_applicable");
        }

        var candidate = utc;
        var held = false;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var nextEnds = windows
                .Select(window => GetQuietWindowEndUtc(window, candidate))
                .Where(end => end.HasValue)
                .Select(end => end!.Value)
                .ToList();
            if (nextEnds.Count == 0)
            {
                return held
                    ? NotificationPolicyDecision.Hold(candidate, "quiet_hours")
                    : NotificationPolicyDecision.Allow("outside_quiet_hours");
            }

            held = true;
            candidate = nextEnds.Max();
        }

        throw new InvalidOperationException("Quiet-hours policy could not resolve a bounded next-allowed time.");
    }

    private static bool IsBypassCategory(string? category, TenantNotificationPolicy? policy)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        var normalized = category.Trim();
        return MandatoryBypassCategories.Any(value => CategoryMatches(normalized, value)) ||
               (policy?.GetBypassCategories().Any(value => CategoryMatches(normalized, value)) ?? false);
    }

    private static bool CategoryMatches(string category, string bypassCategory)
    {
        return category.Equals(bypassCategory, StringComparison.OrdinalIgnoreCase) ||
               category.StartsWith(bypassCategory + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static NotificationPolicyChannel ToPolicyChannel(NotificationChannel channel)
    {
        return channel switch
        {
            NotificationChannel.Email => NotificationPolicyChannel.Email,
            NotificationChannel.Sms => NotificationPolicyChannel.Sms,
            NotificationChannel.Push => NotificationPolicyChannel.Push,
            _ => NotificationPolicyChannel.None
        };
    }

    private static DateTime? GetQuietWindowEndUtc(QuietWindow window, DateTime utc)
    {
        var zone = IanaTimeZoneRules.Find(window.IanaTimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var localMinutes = local.Hour * 60 + local.Minute;
        var crossesMidnight = window.StartMinutes > window.EndMinutes;
        var isQuiet = crossesMidnight
            ? localMinutes >= window.StartMinutes || localMinutes < window.EndMinutes
            : localMinutes >= window.StartMinutes && localMinutes < window.EndMinutes;
        if (!isQuiet)
        {
            return null;
        }

        var endDate = local.Date;
        if (crossesMidnight && localMinutes >= window.StartMinutes)
        {
            endDate = endDate.AddDays(1);
        }

        var endLocal = endDate.AddMinutes(window.EndMinutes);
        return IanaTimeZoneRules.ConvertLocalBoundaryToUtc(
            endLocal,
            zone,
            preferLatestAmbiguousInstant: true);
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private sealed record QuietWindow(string IanaTimeZoneId, int StartMinutes, int EndMinutes);
}

internal sealed record NotificationPolicyDecision(bool ShouldHold, bool Bypassed, DateTime? NextAllowedAtUtc, string Reason)
{
    public static NotificationPolicyDecision Allow(string reason) => new(false, false, null, reason);
    public static NotificationPolicyDecision Bypass(string reason) => new(false, true, null, reason);
    public static NotificationPolicyDecision Hold(DateTime nextAllowedAtUtc, string reason) => new(true, false, nextAllowedAtUtc, reason);
}
