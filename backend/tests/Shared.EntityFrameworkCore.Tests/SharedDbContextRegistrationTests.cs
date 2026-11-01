using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.DependencyInjection;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public sealed class SharedDbContextRegistrationTests
{
    [Fact]
    public void AddDbContext_should_own_shared_context_registration_after_conventional_scan()
    {
        var services = new ServiceCollection();
        ConventionalRegistrar.RegisterAssembly(
            services,
            typeof(SharedDbContextRegistrationTests).Assembly,
            new ConventionalRegistrationOptions
            {
                ExposureMode = ConventionalServiceExposureMode.Default
            });
        services.AddDbContext<RegistrationDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));

        var registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(RegistrationDbContext))
            .ToList();

        var registration = Assert.Single(registrations);
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);

        using var provider = services.BuildServiceProvider();
        RegistrationDbContext firstScopeContext;
        using (var firstScope = provider.CreateScope())
        {
            firstScopeContext = firstScope.ServiceProvider.GetRequiredService<RegistrationDbContext>();
            var repeatedResolution =
                firstScope.ServiceProvider.GetRequiredService<RegistrationDbContext>();

            Assert.Same(firstScopeContext, repeatedResolution);
        }

        using var secondScope = provider.CreateScope();
        var secondScopeContext =
            secondScope.ServiceProvider.GetRequiredService<RegistrationDbContext>();

        Assert.NotSame(firstScopeContext, secondScopeContext);
    }

    public sealed class RegistrationDbContext : SharedDbContext<RegistrationDbContext>
    {
        public RegistrationDbContext(DbContextOptions<RegistrationDbContext> options)
            : base(options)
        {
        }
    }
}
