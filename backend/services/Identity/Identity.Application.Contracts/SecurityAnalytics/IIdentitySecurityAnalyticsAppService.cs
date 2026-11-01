namespace Identity.Application.Contracts.SecurityAnalytics;

public interface IIdentitySecurityAnalyticsAppService
{
    Task<AdminSecurityAnalyticsDto> GetAdminAsync(int? windowDays = null);
    Task<SecurityTimingHeatmapDto> GetAdminTimingHeatmapAsync(int? windowDays = null, string? outcome = null);
    Task<TenantSecurityAnalyticsDto> GetTenantAsync(int? windowDays = null);
}
