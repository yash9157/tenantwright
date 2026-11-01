using Shared.BackgroundTasks.Jobs;

namespace Permissions.Application.Caching;

[BackgroundJobName("permissions.cache-refresh")]
public sealed record PermissionCacheRefreshJobArgs;
