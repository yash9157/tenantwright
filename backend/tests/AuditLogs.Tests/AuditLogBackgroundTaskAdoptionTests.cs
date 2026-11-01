using AuditLogs.Application.Auditing;
using AuditLogs.Application;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Shared.BackgroundTasks.Jobs;
using Shared.DistributedLocking;
using Shared.DistributedLocking.Abstractions;
using Shared.EntityFrameworkCore;
using Shared.Modularity.Modules;
using Shared.Uow.Abstractions;
using Shared.Uow.Attributes;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogBackgroundTaskAdoptionTests
{
    [Fact]
    public void Audit_log_user_triggered_work_uses_shared_background_jobs()
    {
        Assert.True(typeof(IBackgroundJob<AuditLogExportJobArgs>).IsAssignableFrom(typeof(AuditLogExportBackgroundJob)));
        Assert.True(typeof(IBackgroundJob<AuditLogArchiveMaintenanceJobArgs>).IsAssignableFrom(typeof(AuditLogArchiveMaintenanceBackgroundJob)));
    }

    [Fact]
    public void Export_job_orchestrates_independent_phases_without_an_outer_unit_of_work()
    {
        Assert.Null(GetExecuteUnitOfWork<AuditLogExportBackgroundJob, AuditLogExportJobArgs>());
    }

    [Fact]
    public void Archive_maintenance_job_orchestrates_independent_phases_without_an_outer_unit_of_work()
    {
        Assert.Null(GetExecuteUnitOfWork<AuditLogArchiveMaintenanceBackgroundJob, AuditLogArchiveMaintenanceJobArgs>());
    }

    [Fact]
    public void Seal_verification_job_declares_a_non_transactional_outer_unit_of_work()
    {
        var attribute = GetExecuteUnitOfWork<AuditLogSealVerificationBackgroundJob, AuditLogSealVerificationJobArgs>();

        Assert.NotNull(attribute);
        Assert.False(attribute!.IsDisabled);
        Assert.Equal(false, attribute.IsTransactional);
    }

    [Fact]
    public void Export_archive_and_seal_collaborators_resolve_the_automatic_executor_by_interface()
    {
        AssertUsesAutomaticExecutor(typeof(AuditLogExportProcessor));
        AssertUsesAutomaticExecutor(typeof(AuditLogArchiveProcessor));
        AssertUsesAutomaticExecutor(typeof(AuditLogSealService));
    }

    [Fact]
    public void Archive_and_export_background_paths_resolve_dbcontexts_from_the_ambient_uow()
    {
        AssertUsesDbContextProvider(typeof(AuditLogArchiveProcessor));
        AssertUsesDbContextProvider(typeof(AuditLogExportProcessor));
        AssertUsesDbContextProvider(typeof(AuditLogLegalHoldService));

        var archiveParameters = GetConstructorParameterTypes(typeof(AuditLogArchiveProcessor));
        Assert.Contains(typeof(IDistributedLock), archiveParameters);
        Assert.DoesNotContain(typeof(AuditLogsDbContextApp), archiveParameters);
        Assert.DoesNotContain(typeof(AuditLogQueryStore), archiveParameters);
    }

    [Fact]
    public void AuditLogs_application_declares_distributed_locking_as_a_direct_module_dependency()
    {
        var dependencies = typeof(AuditLogsApplicationModule)
            .GetCustomAttributes(typeof(DependsOnAttribute), inherit: false)
            .Cast<DependsOnAttribute>()
            .SelectMany(attribute => attribute.DependedModuleTypes)
            .ToList();

        Assert.Contains(typeof(DistributedLockingModule), dependencies);
    }

    private static UnitOfWorkAttribute? GetExecuteUnitOfWork<TJob, TArgs>()
        where TJob : IBackgroundJob<TArgs>
    {
        var interfaceMap = typeof(TJob).GetInterfaceMap(typeof(IBackgroundJob<TArgs>));
        var executeIndex = Array.FindIndex(
            interfaceMap.InterfaceMethods,
            method => method.Name == nameof(IBackgroundJob<TArgs>.ExecuteAsync));
        Assert.True(executeIndex >= 0);

        return interfaceMap.TargetMethods[executeIndex]
            .GetCustomAttributes(typeof(UnitOfWorkAttribute), inherit: true)
            .Cast<UnitOfWorkAttribute>()
            .SingleOrDefault();
    }

    private static void AssertUsesAutomaticExecutor(Type serviceType)
    {
        var parameterTypes = GetConstructorParameterTypes(serviceType);

        Assert.Contains(typeof(IUnitOfWorkExecutor), parameterTypes);
        Assert.DoesNotContain(typeof(IUnitOfWorkManager), parameterTypes);
    }

    private static void AssertUsesDbContextProvider(Type serviceType)
    {
        Assert.Contains(
            typeof(IDbContextProvider<AuditLogsDbContextApp>),
            GetConstructorParameterTypes(serviceType));
    }

    private static List<Type> GetConstructorParameterTypes(Type serviceType)
    {
        return serviceType
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();
    }
}
