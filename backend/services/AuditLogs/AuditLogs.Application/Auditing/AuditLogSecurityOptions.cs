namespace AuditLogs.Application.Auditing;

public sealed class AuditLogSecurityOptions
{
    public const string SectionName = "AuditLogging:Security";

    public bool SendExceptionDetailsToClients { get; set; }

    public bool SendExceptionStackTraceToClients { get; set; }

    public bool SendExceptionTypeToClients { get; set; }

    public string HiddenExceptionMessage { get; set; } = "Exception details are hidden.";
}

public static class AuditLogSecurityOptionsPostConfigure
{
    public static void Normalize(AuditLogSecurityOptions options)
    {
        options ??= new AuditLogSecurityOptions();

        if (!options.SendExceptionDetailsToClients)
        {
            options.SendExceptionStackTraceToClients = false;
        }

        options.HiddenExceptionMessage = string.IsNullOrWhiteSpace(options.HiddenExceptionMessage)
            ? "Exception details are hidden."
            : options.HiddenExceptionMessage.Trim();
    }
}
