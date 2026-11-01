using Microsoft.EntityFrameworkCore;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EntityFrameworkCore.ValueComparers;
using Shared.EntityFrameworkCore.ValueConverters;
using Shared.MultiTenancy;
using Tenant.Domain.Tenants;
using Tenant.Domain.Shared.Constants;
using Tenant.Domain.Shared.Enums;

namespace Tenant.EntityFrameworkCore.EntityFrameworkCore;

[IgnoreMultiTenancy]
[ConnectionStringName(TenantWrightDatabaseCatalog.Tenant)]
public class TenantDbContextApp : SharedDbContext<TenantDbContextApp>
{
    public const string TenantConnectionStringTargetIndexName = "IX_TenantConnectionStrings_TargetFingerprint";

    public DbSet<Domain.Tenants.Tenant> Tenants { get; set; } = default!;
    public DbSet<TenantConnectionString> TenantConnectionStrings { get; set; } = default!;
    public DbSet<TenantLifecycleTransition> TenantLifecycleTransitions { get; set; } = default!;
    public DbSet<TenantOwnershipRequest> TenantOwnershipRequests { get; set; } = default!;
    public DbSet<TenantClosureRequest> TenantClosureRequests { get; set; } = default!;

    public TenantDbContextApp(DbContextOptions<TenantDbContextApp> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddTenantWrightMessagingEntities();

        modelBuilder.Entity<Domain.Tenants.Tenant>(b =>
        {
            b.ToTable("Tenants");
            b.HasKey(x => x.Id);

            b.Property(x => x.Name).IsRequired().HasMaxLength(TenantConsts.MaxNameLength);
            b.Property(x => x.NormalizedName).IsRequired().HasMaxLength(TenantConsts.MaxNormalizedNameLength);
            b.Property(x => x.IsActive).IsRequired();
            b.Property(x => x.LifecycleStatus).HasConversion<int>().IsRequired();
            b.Property(x => x.LifecycleRevision).IsRequired();
            b.ToTable(t => t.HasCheckConstraint(
                "CK_Tenants_LifecycleRevision_Positive",
                "\"LifecycleRevision\" >= 1"));
            b.Property(x => x.OwnerUserId);
            b.Property(x => x.OwnerDisplayName).HasMaxLength(256); b.Property(x => x.OwnerEmail).HasMaxLength(256);
            b.Property(x => x.ContactName).HasMaxLength(128); b.Property(x => x.ContactEmail).HasMaxLength(256); b.Property(x => x.ContactPhone).HasMaxLength(32);
            b.Property(x => x.AddressLine1).HasMaxLength(256); b.Property(x => x.AddressLine2).HasMaxLength(256); b.Property(x => x.City).HasMaxLength(128); b.Property(x => x.StateOrUnionTerritory).HasMaxLength(64); b.Property(x => x.PinCode).HasMaxLength(6);
            b.Property(x => x.LegalEntityName).HasMaxLength(200); b.Property(x => x.RegistrationNumber).HasMaxLength(64); b.Property(x => x.TaxId).HasMaxLength(32); b.Property(x => x.Accreditation).HasMaxLength(128); b.Property(x => x.DataRegion).HasMaxLength(32); b.Property(x => x.RetentionPolicyDays);
            b.Property(x => x.ConcurrencyStamp).IsRequired().HasMaxLength(64);

            b.Property(x => x.ExtraProperties)
                .HasConversion(new ExtraPropertyDictionaryValueConverter())
                .Metadata.SetValueComparer(new ExtraPropertyDictionaryValueComparer());

            // Uniqueness applies only to live tenants, so a soft-deleted tenant's
            // name can be reused. This matches the app-level name check, which
            // already filters soft-deleted tenants.
            b.HasIndex(x => x.NormalizedName).IsUnique().HasFilter("\"IsDeleted\" = false");

            b.HasMany(x => x.ConnectionStrings)
                .WithOne()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantConnectionString>(b =>
        {
            b.ToTable("TenantConnectionStrings");
            b.HasKey(x => new { x.TenantId, x.Name });

            b.Property(x => x.Name).IsRequired().HasMaxLength(TenantConsts.MaxConnectionStringNameLength);
            b.Property(x => x.Value);
            b.Property(x => x.ProtectedValue);
            b.Property(x => x.ProtectionKeyId).HasMaxLength(128);
            b.Property(x => x.ProtectionKeyVersion);
            b.Property(x => x.TargetFingerprint).HasMaxLength(TenantConsts.MaxConnectionTargetFingerprintLength);

            // One physical database can back at most one dedicated assignment across every tenant and name; the
            // application check gives the friendly error, this index closes the race between concurrent assignments.
            b.HasIndex(x => x.TargetFingerprint)
                .IsUnique()
                .HasDatabaseName(TenantConnectionStringTargetIndexName)
                .HasFilter("\"TargetFingerprint\" IS NOT NULL");
        });

        modelBuilder.Entity<TenantLifecycleTransition>(b =>
        {
            b.ToTable("TenantLifecycleTransitions");
            b.HasKey(x => x.Id);
            b.Property(x => x.TenantId).IsRequired();
            b.Property(x => x.PreviousStatus).HasConversion<int>().IsRequired();
            b.Property(x => x.CurrentStatus).HasConversion<int>().IsRequired();
            b.Property(x => x.ReasonCode).IsRequired().HasMaxLength(64);
            b.Property(x => x.Note).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.CreationTime });
        });

        modelBuilder.Entity<TenantOwnershipRequest>(b =>
        {
            b.ToTable("TenantOwnershipRequests");
            b.HasKey(x => x.Id);
            b.Property(x => x.TargetEmail).HasMaxLength(256).IsRequired();
            b.Property(x => x.SuggestedUserName).HasMaxLength(256);
            b.Property(x => x.ReasonCode).HasMaxLength(64).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500);
            b.Property(x => x.Kind).HasConversion<int>().IsRequired();
            b.Property(x => x.Status).HasConversion<int>().IsRequired();
            b.Property(x => x.WorkflowReconciledStatus).HasConversion<int>();
            b.Property(x => x.ExpiresAtUtc).IsRequired();
            b.Property(x => x.RegistrationOrigin).HasConversion<int>().IsRequired();
            b.Property(x => x.RegistrationOperationId);
            b.Property(x => x.InvitationCreatedAtUtc);
            b.Property(x => x.ProvisioningRetryCount).IsRequired();
            b.Property(x => x.LastProvisioningRetryAtUtc);
            b.Property(x => x.PermissionsProvisionedAtUtc);
            b.Property(x => x.ResendCount).IsRequired();
            b.Property(x => x.LastResentAtUtc);
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasIndex(x => x.RegistrationOperationId).IsUnique().HasFilter("\"RegistrationOperationId\" IS NOT NULL");
            // Supports the onboarding-funnel cohort scan (Kind = InitialOwner within a
            // creation-time window) without an unbounded table scan.
            b.HasIndex(x => new { x.Kind, x.CreationTime });
            b.HasIndex(x => new { x.Kind, x.CreationTime, x.Id })
                .HasFilter("\"WorkflowReconciledStatus\" IS NULL OR \"WorkflowReconciledStatus\" <> \"Status\"");
        });

        modelBuilder.Entity<TenantClosureRequest>(b =>
        {
            b.ToTable("TenantClosureRequests"); b.HasKey(x => x.Id);
            b.Property(x => x.ReasonCode).HasMaxLength(64).IsRequired(); b.Property(x => x.Note).HasMaxLength(500);
            b.Property(x => x.DecisionReasonCode).HasMaxLength(64); b.Property(x => x.DecisionNote).HasMaxLength(500);
            b.Property(x => x.Status).HasConversion<int>().IsRequired(); b.HasIndex(x => new { x.TenantId, x.Status });
        });
    }
}
