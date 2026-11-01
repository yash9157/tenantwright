namespace Tenant.Application.Contracts.Dashboard;

public interface ITenantDashboardAppService
{
    Task<AdminTenantDashboardDto> GetAdminAsync(int? windowDays = null);
    Task<TenantOnboardingFunnelDto> GetOnboardingFunnelAsync(int? windowDays = null, int? stallThresholdHours = null);
    Task<CurrentTenantDashboardDto> GetCurrentAsync();
}
