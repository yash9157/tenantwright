using Microsoft.EntityFrameworkCore;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;
using Shared.MultiTenancy;

namespace Auth.EntityFrameworkCore;

[ConnectionStringName(TenantWrightDatabaseCatalog.Auth)]
[IgnoreMultiTenancy]
public class AuthDbContext : DbContext, IEfCoreDbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
    }

    public Task<int> SaveChangesOnDbContextAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default) =>
        base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.UseOpenIddict();
        modelBuilder.AddTenantWrightMessagingEntities();
    }
}
