using Shared.BackgroundTasks.Jobs;

namespace Identity.Application.Users;

[BackgroundJobName("identity.user-export")]
public sealed record IdentityUserExportJobArgs(
    Guid? TenantId,
    long JobId,
    Guid RequestedByUserId) : IBackgroundJobTenantArgs;

[BackgroundJobName("identity.user-import")]
public sealed record IdentityUserImportJobArgs(
    Guid? TenantId,
    long JobId,
    Guid RequestedByUserId) : IBackgroundJobTenantArgs;
