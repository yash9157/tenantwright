using Identity.Controllers.InternalPermissionTargets;
using Identity.Controllers.InternalSessions;
using Identity.Controllers.Account;
using Identity.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shared.MultiTenancy;
using Shared.BackgroundTasks.Jobs;
using Shared.Uow.Attributes;
using Xunit;

namespace Identity.Tests;

public class IdentityHostSurfaceTests
{
    [Fact]
    public void Identity_host_does_not_expose_sample_weather_endpoint()
    {
        var controllerTypes = typeof(global::Identity.IdentityHostModule).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToArray();

        Assert.DoesNotContain("Identity.Controllers.WeatherForecastController", controllerTypes);
    }

    [Fact]
    public void Internal_permission_target_controller_uses_identity_internal_policy()
    {
        var authorize = Assert.Single(typeof(InternalPermissionTargetsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal("IdentityInternal", authorize.Policy);
    }

    [Fact]
    public void Internal_session_count_controller_uses_the_m2m_only_identity_policy()
    {
        var authorize = Assert.Single(typeof(InternalSessionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal("IdentityInternal", authorize.Policy);
    }

    [Fact]
    public void User_info_by_id_uses_self_or_internal_policy_without_anonymous_bypass()
    {
        var authorize = Assert.Single(typeof(UserInfoController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal("IdentitySelfOrInternal", authorize.Policy);
        Assert.Empty(typeof(UserInfoController)
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Fact]
    public void Identity_import_export_use_shared_background_jobs_not_host_pollers()
    {
        Assert.True(typeof(IBackgroundJob<IdentityUserImportJobArgs>).IsAssignableFrom(typeof(IdentityUserImportBackgroundJob)));
        Assert.True(typeof(IBackgroundJob<IdentityUserExportJobArgs>).IsAssignableFrom(typeof(IdentityUserExportBackgroundJob)));

        var hostTypes = typeof(global::Identity.IdentityHostModule).Assembly
            .GetTypes()
            .Select(type => type.FullName)
            .ToArray();

        Assert.DoesNotContain("Identity.Workers.IdentityUserImportHostedService", hostTypes);
        Assert.DoesNotContain("Identity.Workers.IdentityUserExportHostedService", hostTypes);
    }

    [Fact]
    public void Identity_import_job_declares_a_non_transactional_unit_of_work()
    {
        AssertNonTransactionalJob<IdentityUserImportBackgroundJob, IdentityUserImportJobArgs>();
    }

    [Fact]
    public void Identity_export_job_declares_a_non_transactional_unit_of_work()
    {
        AssertNonTransactionalJob<IdentityUserExportBackgroundJob, IdentityUserExportJobArgs>();
    }

    [Fact]
    public void Identity_rate_limit_partition_uses_resolved_tenant_not_the_request_header()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ICurrentTenant>(new FixedCurrentTenant(tenantId))
                .BuildServiceProvider()
        };
        context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString("D");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;

        var key = global::Identity.IdentityHostModule.BuildIdentityPartitionKey(context, "identity");

        Assert.Contains(tenantId.ToString("D"), key, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Request.Headers["X-Tenant-Id"].ToString(), key, StringComparison.Ordinal);
    }

    private static void AssertNonTransactionalJob<TJob, TArgs>()
        where TJob : IBackgroundJob<TArgs>
    {
        var interfaceMap = typeof(TJob).GetInterfaceMap(typeof(IBackgroundJob<TArgs>));
        var executeIndex = Array.FindIndex(
            interfaceMap.InterfaceMethods,
            method => method.Name == nameof(IBackgroundJob<TArgs>.ExecuteAsync));
        Assert.True(executeIndex >= 0);

        var attribute = interfaceMap.TargetMethods[executeIndex]
            .GetCustomAttributes(typeof(UnitOfWorkAttribute), inherit: true)
            .Cast<UnitOfWorkAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.False(attribute!.IsDisabled);
        Assert.Equal(false, attribute.IsTransactional);
    }

    private sealed class FixedCurrentTenant(Guid? tenantId) : ICurrentTenant
    {
        public bool IsAvailable => tenantId.HasValue;
        public Guid? Id => tenantId;
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => throw new NotSupportedException();
    }
}
