using System;
using Microsoft.Extensions.Configuration;
using Shared.AspNetCore.Host.Configuration;

namespace Auth.Authorization;

/// <summary>
/// Decides whether the OAuth 2.0 resource-owner-password grant may be enabled.
/// The grant is only ever permitted in Development. Outside Development an explicit
/// opt-in is treated as a startup configuration error rather than being honoured, so
/// the password flow can never be silently enabled in a deployed environment, and a
/// missing/absent setting resolves to disabled (fail-closed).
/// </summary>
public static class AuthPasswordFlowPolicy
{
    public const string ConfigurationKey = "OpenIddict:AllowPasswordFlow";

    /// <summary>
    /// Returns whether the password flow should be registered.
    /// Throws <see cref="InvalidOperationException"/> when it is explicitly requested
    /// outside Development.
    /// </summary>
    public static bool ResolveEnabled(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // In Development the flow is on unless explicitly turned off; everywhere else it
        // requires an explicit "true", which is then rejected below.
        var requested = isDevelopment
            ? !string.Equals(configuration[ConfigurationKey], "false", StringComparison.OrdinalIgnoreCase)
            : string.Equals(configuration[ConfigurationKey], "true", StringComparison.OrdinalIgnoreCase);

        if (requested && !isDevelopment)
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: "tenantwright-auth",
                key: ConfigurationKey,
                rule: "Must be disabled outside Development.",
                fixHint: "Set 'OpenIddict__AllowPasswordFlow=false' in charts/tenantwright-auth/values.yaml (or appsettings) and redeploy.");
        }

        return requested;
    }
}
