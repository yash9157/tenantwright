using System;
using Shared.AuditLogs.Auditing;
using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public class IdentitySecurityLog : IMultiTenant
{
    public const int IndexedIdentifierMaxLength = 256;
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string? ApplicationName { get; set; }
    public string? Identity { get; set; }
    public string? Action { get; set; }
    public string? SessionId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string? ClientId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? BrowserInfo { get; set; }
    public string? Location { get; set; }
    public string? RiskLevel { get; set; }
    public string? RiskAction { get; set; }
    public int? RiskScore { get; set; }
    public int? RiskReasonFlags { get; set; }
    public bool? MfaEnrolled { get; set; }
    public bool? BlockedByRisk { get; set; }
    public bool? FalsePositive { get; set; }
    public string? CountryCode { get; set; }
    public Guid? RiskOverrideId { get; set; }
    public string? ExtraProperties { get; set; }
    public DateTime CreationTime { get; set; }

    public void NormalizeForPersistence()
    {
        ApplicationName = Truncate(ApplicationName);
        Identity = Truncate(Identity);
        UserName = Truncate(UserName);
    }

    private static string? Truncate(string? value)
        => value is { Length: > IndexedIdentifierMaxLength }
            ? value[..IndexedIdentifierMaxLength]
            : value;
}
