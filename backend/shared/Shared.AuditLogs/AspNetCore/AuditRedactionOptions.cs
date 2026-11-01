namespace Shared.AuditLogs.AspNetCore;

public sealed class AuditRedactionOptions
{
    public const string SectionPath = "AuditLogging:Redaction";

    public string Mask { get; set; } = "***REDACTED***";

    public List<string> SensitiveKeys { get; set; } =
    [
        "password",
        "secret",
        "token",
        "access_token",
        "refresh_token",
        "id_token",
        "authorization",
        "cookie",
        "api_key",
        "apikey",
        "creditcard",
        "ssn",
        "nationalid",
        "otp",
        "totp",
        "twofactorcode",
        "twofactorrecoverycode",
        "recoverycode",
        "code_verifier",
        "challengeid",
        // Connection strings embed credentials (for example a tenant's dedicated database assignment input).
        "connectionstring"
    ];
}

public static class AuditRedactionOptionsPostConfigure
{
    public static void Normalize(AuditRedactionOptions options)
    {
        options ??= new AuditRedactionOptions();

        options.Mask = string.IsNullOrWhiteSpace(options.Mask)
            ? "***REDACTED***"
            : options.Mask.Trim();

        var normalizedKeys = options.SensitiveKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedKeys.Count == 0)
        {
            normalizedKeys.AddRange(
            [
                "password",
                "secret",
                "token",
                "authorization"
            ]);
        }

        options.SensitiveKeys = normalizedKeys;
    }
}
