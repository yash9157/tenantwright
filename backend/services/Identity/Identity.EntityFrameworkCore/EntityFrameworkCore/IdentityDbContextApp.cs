using Identity.Domain.ApplicationUsers;
using Identity.Domain.SecuritySettings;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;
using Shared.MultiTenancy;

namespace Identity.EntityFrameworkCore.EntityFrameworkCore;

[IgnoreMultiTenancy]
[ConnectionStringName(TenantWrightDatabaseCatalog.Identity)]
public class IdentityDbContextApp : SharedDbContext<IdentityDbContextApp>
{

    public DbSet<ApplicationUser> Users { get; set; } = default!;
    public DbSet<ApplicationRole> Roles { get; set; } = default!;
    public DbSet<IdentityUserClaim<Guid>> UserClaims { get; set; } = default!;
    public DbSet<IdentityUserRole<Guid>> UserRoles { get; set; } = default!;
    public DbSet<IdentityUserLogin<Guid>> UserLogins { get; set; } = default!;
    public DbSet<IdentityUserToken<Guid>> UserTokens { get; set; } = default!;
    public DbSet<IdentityRoleClaim<Guid>> RoleClaims { get; set; } = default!;

    public DbSet<OrganizationUnit> OrganizationUnits { get; set; } = default!;
    public DbSet<OrganizationUnitRole> OrganizationUnitRoles { get; set; } = default!;
    public DbSet<UserOrganizationUnit> UserOrganizationUnits { get; set; } = default!;
    public DbSet<IdentitySecurityLog> IdentitySecurityLogs { get; set; } = default!;
    public DbSet<IdentityLinkUser> IdentityLinkUsers { get; set; } = default!;
    public DbSet<IdentityLinkUserApproval> IdentityLinkUserRequests { get; set; } = default!;
    public DbSet<IdentityUserDelegation> IdentityUserDelegations { get; set; } = default!;
    public DbSet<IdentityUserNotificationSecurityPreference> IdentityUserNotificationSecurityPreferences { get; set; } = default!;
    public DbSet<IdentityUserLifecycleHistory> IdentityUserLifecycleHistory { get; set; } = default!;
    public DbSet<IdentityUserExportJob> IdentityUserExportJobs { get; set; } = default!;
    public DbSet<IdentityUserImportJob> IdentityUserImportJobs { get; set; } = default!;
    public DbSet<IdentitySession> IdentitySessions { get; set; } = default!;
    public DbSet<IdentityLoginRiskOverride> IdentityLoginRiskOverrides { get; set; } = default!;
    public DbSet<IdentityTrustedDevice> IdentityTrustedDevices { get; set; } = default!;
    public DbSet<IdentityPasskeyCredential> IdentityPasskeyCredentials { get; set; } = default!;
    public DbSet<IdentityPublicInvitation> IdentityPublicInvitations { get; set; } = default!;
    public DbSet<IdentityClaimType> IdentityClaimTypes { get; set; } = default!;
    public DbSet<HostIdentitySecurityPolicy> HostIdentitySecurityPolicies { get; set; } = default!;

    public IdentityDbContextApp(DbContextOptions<IdentityDbContextApp> options)
        : base(options)
    {
    }

    public bool IsDetached<TEntity>(TEntity entity) where TEntity : class
    {
        return Entry(entity).State == EntityState.Detached;
    }

    public bool IsRelational()
    {
        return Database.IsRelational();
    }

    public void ClearChangeTracker()
    {
        ChangeTracker.Clear();
    }

    public async Task<IDbContextTransaction?> BeginTransactionIfRelationalAsync(CancellationToken cancellationToken = default)
    {
        return Database.IsRelational()
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<IdentitySecurityLog>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.NormalizeForPersistence();
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddTenantWrightMessagingEntities();
        modelBuilder.ConfigureIdentityModel();

        modelBuilder.Entity<ApplicationRole>(b =>
        {
            b.Property(x => x.Description).HasMaxLength(512);
            b.Property(x => x.Category).HasMaxLength(64);
            b.Property(x => x.IsSensitive).HasDefaultValue(false);
            b.Property(x => x.LifecycleState).HasDefaultValue(Identity.Domain.Shared.ApplicationUsers.ApplicationRoleLifecycleState.Active);
            b.Property(x => x.LifecycleVersion).HasDefaultValue(0L).IsConcurrencyToken();
            b.Property(x => x.RetirementRequestedAtUtc);
            b.Property(x => x.RetirementRequestId);
            b.HasIndex(x => x.RetirementRequestId).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LifecycleState });
        });

        modelBuilder.Entity<IdentityPublicInvitation>(b =>
        {
            b.ToTable("PublicInvitations");
            b.HasKey(x => x.Id);
            b.Property(x => x.TenantName).HasMaxLength(64).IsRequired();
            b.Property(x => x.Email).HasMaxLength(256).IsRequired();
            b.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            b.Property(x => x.RoleName).HasMaxLength(64);
            b.Property(x => x.SuggestedUserName).HasMaxLength(256);
            b.Property(x => x.CancelReason).HasMaxLength(64);
            b.Property(x => x.OwnershipRequestId);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Email });
            b.HasIndex(x => x.ExpiresAtUtc);
            b.HasIndex(x => x.OwnershipRequestId).IsUnique();
        });
    }

}
