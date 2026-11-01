using System.Reflection;
using AuditLogs.Application;
using Identity.Application;
using Microsoft.EntityFrameworkCore;
using Notifications.Application;
using Permissions.Application;
using Tenant.Application;
using Workflows.Application;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public sealed class ApplicationDbContextOwnershipTests
{
    private static readonly Assembly[] ApplicationAssemblies =
    [
        typeof(AuditLogsApplicationModule).Assembly,
        typeof(IdentityApplicationModule).Assembly,
        typeof(NotificationsApplicationModule).Assembly,
        typeof(PermissionsApplicationModule).Assembly,
        typeof(TenantApplicationModule).Assembly,
        typeof(WorkflowsApplicationModule).Assembly
    ];

    [Fact]
    public void Application_types_must_not_take_concrete_db_context_dependencies()
    {
        var violations = ApplicationAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .SelectMany(constructor => constructor.GetParameters()
                .Where(parameter => typeof(DbContext).IsAssignableFrom(parameter.ParameterType))
                .Select(parameter => $"{constructor.DeclaringType!.FullName} -> {parameter.ParameterType.FullName}"))
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Application constructors must obtain DbContexts from IDbContextProvider<T> inside an active unit of work. " +
            $"Concrete dependencies found:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }
}
