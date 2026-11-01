using Identity.Domain.Shared.Account;

namespace Identity.Application.Contracts.Account;

public static class UserInfoEtagHelper
{
    public static string Build(UserInfoDto userInfo, UserInfoSegments segments)
    {
        var includeProfile = segments.HasFlag(UserInfoSegments.Profile);
        var includeAuthz = segments.HasFlag(UserInfoSegments.Authorization);

        var profile = includeProfile ? (userInfo.ProfileVersion ?? "-") : "-";
        var authz = includeAuthz ? (userInfo.AuthzVersion ?? "-") : "-";
        return $"W/\"p:{profile}|a:{authz}\"";
    }

    public static bool Matches(string ifNoneMatchHeader, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatchHeader) || string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        if (string.Equals(ifNoneMatchHeader.Trim(), "*", StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = ifNoneMatchHeader
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (string.Equals(token, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static UserInfoSegments ParseSegments(string? rawSegments)
    {
        if (string.IsNullOrWhiteSpace(rawSegments))
        {
            return UserInfoSegments.Profile | UserInfoSegments.Authorization;
        }

        var segments = UserInfoSegments.None;
        var tokens = rawSegments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (string.Equals(token, "profile", StringComparison.OrdinalIgnoreCase))
            {
                segments |= UserInfoSegments.Profile;
            }
            else if (string.Equals(token, "authz", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(token, "authorization", StringComparison.OrdinalIgnoreCase))
            {
                segments |= UserInfoSegments.Authorization;
            }
        }

        return segments == UserInfoSegments.None
            ? UserInfoSegments.Profile | UserInfoSegments.Authorization
            : segments;
    }

    public static string ToQueryString(UserInfoSegments segments)
    {
        if (segments == (UserInfoSegments.Profile | UserInfoSegments.Authorization))
        {
            return "profile,authz";
        }

        if (segments == UserInfoSegments.Profile)
        {
            return "profile";
        }

        if (segments == UserInfoSegments.Authorization)
        {
            return "authz";
        }

        return "profile,authz";
    }
}
