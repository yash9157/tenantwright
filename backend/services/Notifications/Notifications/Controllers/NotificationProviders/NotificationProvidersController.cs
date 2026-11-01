using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Providers;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Notifications;

namespace Notifications.Controllers.NotificationProviders;

[ApiController]
[Route("api/notifications/providers")]
[Authorize]
public class NotificationProvidersController : ControllerBase
{
    private readonly INotificationProviderDiagnosticsAppService _notificationProviderDiagnosticsAppService;

    public NotificationProvidersController(INotificationProviderDiagnosticsAppService notificationProviderDiagnosticsAppService)
    {
        _notificationProviderDiagnosticsAppService = notificationProviderDiagnosticsAppService;
    }

    [HttpGet("summary")]
    [Authorize(Policy = NotificationsPermissions.Providers.Read)]
    public Task<NotificationProviderDiagnosticsSummaryDto> GetSummaryAsync([FromQuery] Guid? tenantId, CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.GetSummaryAsync(tenantId, cancellationToken);
    }

    [HttpGet("secret-rotation")]
    [Authorize(Policy = NotificationsPermissions.Providers.Read)]
    public Task<NotificationProviderSecretRotationStatusDto> GetSecretRotationStatusAsync(
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.GetSecretRotationStatusAsync(tenantId, cancellationToken);
    }

    [HttpPost("{configurationId:guid}/live-verification")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.TestSend)]
    public Task<NotificationProviderLiveVerificationDto> RecordLiveVerificationAsync(
        Guid configurationId,
        [FromBody] RecordNotificationProviderLiveVerificationInput input,
        CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.RecordLiveVerificationAsync(
            configurationId,
            input,
            cancellationToken);
    }

    [HttpGet("activity")]
    [Authorize(Policy = NotificationsPermissions.Providers.ActivityRead)]
    public Task<PagedResultDto<NotificationProviderActivityItemDto>> GetActivityAsync(
        [FromQuery] GetNotificationProviderActivityInput input,
        CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.GetRecentActivityAsync(input, cancellationToken);
    }

    [HttpGet("analytics")]
    [Authorize(Policy = NotificationsPermissions.Providers.AnalyticsRead)]
    public Task<NotificationProviderAnalyticsDto> GetAnalyticsAsync(
        [FromQuery] GetNotificationProviderAnalyticsInput input,
        CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.GetAnalyticsAsync(input, cancellationToken);
    }

    [HttpGet("analytics/latency")]
    [Authorize(Policy = NotificationsPermissions.Providers.AnalyticsRead)]
    public Task<NotificationProviderLatencyReportDto> GetLatencyAsync(
        [FromQuery] GetNotificationProviderLatencyInput input,
        CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.GetLatencyAsync(input, cancellationToken);
    }

    [HttpGet("current-tenant/analytics")]
    [Authorize(Policy = NotificationsPermissions.Providers.AnalyticsRead)]
    public Task<NotificationProviderAnalyticsDto> GetCurrentTenantAnalyticsAsync(
        [FromQuery] GetCurrentTenantNotificationProviderAnalyticsInput input,
        CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.GetCurrentTenantAnalyticsAsync(input, cancellationToken);
    }

    [HttpGet("current-tenant/analytics/export")]
    [Authorize(Policy = NotificationsPermissions.Providers.AnalyticsRead)]
    public Task<NotificationProviderAnalyticsExportDto> ExportCurrentTenantAnalyticsAsync(
        [FromQuery] GetCurrentTenantNotificationProviderAnalyticsExportInput input,
        CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.ExportCurrentTenantAnalyticsAsync(input, cancellationToken);
    }

    [HttpGet("analytics/export")]
    [Authorize(Policy = NotificationsPermissions.Providers.AnalyticsRead)]
    public Task<NotificationProviderAnalyticsExportDto> ExportAnalyticsAsync(
        [FromQuery] GetNotificationProviderAnalyticsExportInput input,
        CancellationToken cancellationToken)
    {
        return _notificationProviderDiagnosticsAppService.ExportAnalyticsAsync(input, cancellationToken);
    }
}
