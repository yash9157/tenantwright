using AuditLogs.Controllers.AuditLogs;
using Microsoft.AspNetCore.Authorization;
using Shared.Permissions.Definitions.AuditLogs;
using Xunit;

namespace AuditLogs.Tests;

public sealed class AuditLogsControllerAuthorizationTests
{
    [Theory]
    [InlineData(nameof(AuditLogsController.GetArchiveManifestsAsync), AuditLogsPermissions.Archive.Read)]
    [InlineData(nameof(AuditLogsController.GetArchiveRestoreJobsAsync), AuditLogsPermissions.Archive.Read)]
    [InlineData(nameof(AuditLogsController.GetArchiveRestoreJobAsync), AuditLogsPermissions.Archive.Read)]
    [InlineData(nameof(AuditLogsController.RequestArchiveRestoreAsync), AuditLogsPermissions.Archive.Restore)]
    [InlineData(nameof(AuditLogsController.CancelArchiveRestoreJobAsync), AuditLogsPermissions.Archive.Restore)]
    [InlineData(nameof(AuditLogsController.RetryArchiveRestoreJobAsync), AuditLogsPermissions.Archive.Restore)]
    [InlineData(nameof(AuditLogsController.VerifySealRangeAsync), AuditLogsPermissions.Archive.VerifyIntegrity)]
    [InlineData(nameof(AuditLogsController.GetSealVerificationsAsync), AuditLogsPermissions.Archive.VerifyIntegrity)]
    [InlineData(nameof(AuditLogsController.GetSealVerificationAsync), AuditLogsPermissions.Archive.VerifyIntegrity)]
    [InlineData(nameof(AuditLogsController.GetLegalHoldsAsync), AuditLogsPermissions.LegalHold.Read)]
    [InlineData(nameof(AuditLogsController.CreateLegalHoldAsync), AuditLogsPermissions.LegalHold.Create)]
    [InlineData(nameof(AuditLogsController.ReleaseLegalHoldAsync), AuditLogsPermissions.LegalHold.Release)]
    [InlineData(nameof(AuditLogsController.GetSensitivePayloadAsync), AuditLogsPermissions.SensitivePayload.Read)]
    public void Endpoint_should_require_the_precise_audit_permission(string methodName, string expectedPolicy)
    {
        var method = typeof(AuditLogsController).GetMethods()
            .Single(x => x.Name == methodName);
        var authorize = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal(expectedPolicy, authorize.Policy);
    }
}
