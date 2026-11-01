using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared;

namespace Identity.Application.Contracts.SecurityLogs;

/// <remarks>
/// The sortable set mirrors <c>SecurityLogQueryExtensions.ApplySorting</c>. The default keeps the newest
/// entry first, which is the order an operator investigating an incident expects.
/// </remarks>
[SortableFields(
    "CreationTime",
    "UserName",
    "Action",
    "RiskScore",
    DefaultSort = "CreationTime desc")]
public class SecurityLogListInput
{
    public string? UserName { get; set; }
    public string? ClientId { get; set; }
    public string? Action { get; set; }
    public string? Sorting { get; set; }
    public string? RiskLevel { get; set; }
    public string? RiskAction { get; set; }
    public bool? BlockedByRisk { get; set; }
    public bool? FalsePositive { get; set; }
    public string? CountryCode { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? ApplicationName { get; set; }
    public string? Identity { get; set; }
    public Guid? UserId { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

public class PagedResultDto<T>
{
    public long TotalCount { get; set; }
    public List<T> Items { get; set; } = [];
}

public class SecurityLogDto
{
    public Guid Id { get; set; }
    public string? ApplicationName { get; set; }
    public string? Identity { get; set; }
    public string? Action { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string? ClientId { get; set; }
    public string? ClientIpAddress { get; set; }
    public DateTime CreationTime { get; set; }
    public string? RiskLevel { get; set; }
    public string? RiskAction { get; set; }
    public int? RiskScore { get; set; }
    public List<string> RiskReasons { get; set; } = [];
    public bool? BlockedByRisk { get; set; }
    public bool? FalsePositive { get; set; }
    public string? CountryCode { get; set; }
    public Guid? RiskOverrideId { get; set; }
}

public sealed class SecurityLogDetailDto : SecurityLogDto
{
    public string? SessionId { get; set; }
    public string? CorrelationId { get; set; }
    public string? BrowserInfo { get; set; }
    public string? Location { get; set; }
}

public interface ISecurityLogAppService
{
    Task<SecurityLogDto> GetAsync(Guid id);
    Task<SecurityLogDetailDto> GetDetailAsync(Guid id);
    Task<List<SecurityLogDto>> GetListAsync(string? userName = null, string? clientId = null);
    Task<PagedResultDto<SecurityLogDto>> GetPagedListAsync(SecurityLogListInput input);
}
