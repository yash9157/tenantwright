using Shared.AuditLogs.Auditing;
using Shared.Uow.Abstractions;
using Tenant.Application.Tenants;
using Xunit;

namespace Tenant.Tests;

/// <summary>
/// Unit-of-work and auditing interception uses Castle class proxies, which subclass the implementation. A sealed
/// intercepted type does not fail at build or in container-free tests; it crashes the host at startup when the proxy
/// type is emitted. This guard turns that into a test failure.
/// </summary>
public sealed class InterceptedTypeShapeTests
{
    [Fact]
    public void Intercepted_application_types_are_not_sealed()
    {
        var sealedIntercepted = typeof(TenantDatabaseAppService).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsSealed: true })
            .Where(type => typeof(IUnitOfWorkEnabled).IsAssignableFrom(type) ||
                           typeof(IAuditingEnabled).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(sealedIntercepted);
    }
}
