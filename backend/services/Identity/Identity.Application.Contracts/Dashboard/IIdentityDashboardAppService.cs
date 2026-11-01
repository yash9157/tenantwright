namespace Identity.Application.Contracts.Dashboard;

public interface IIdentityDashboardAppService
{
    Task<AdminIdentityDashboardDto> GetAdminAsync(int? windowDays = null);
    Task<MfaAdoptionDto> GetMfaAdoptionAsync(int? top = null);
    Task<TenantIdentityDashboardDto> GetTenantAsync(int? windowDays = null);
}
