using Identity.Domain.ApplicationUsers;
using Identity.Domain.SecuritySettings;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Identity.EntityFrameworkCore.EntityFrameworkCore;

public static class IdentityModelBuilderExtensions
{
    public static void ConfigureIdentityModel(this ModelBuilder builder)
    {
        ConfigureAspNetIdentity(builder);
        ConfigureIdentityDomain(builder);
    }

    private static void ConfigureAspNetIdentity(ModelBuilder builder)
    {
        builder.Entity<ApplicationUser>(b =>
        {
            b.ToTable("AspNetUsers", table =>
                table.HasCheckConstraint(
                    "CK_AspNetUsers_LifecycleStatus",
                    "\"LifecycleStatus\" IN (0, 1, 2, 3, 4)"));
            b.HasKey(u => u.Id);
            b.HasIndex(u => u.TenantId);

            b.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(u => u.UserName).HasMaxLength(256);
            b.Property(u => u.NormalizedUserName).HasMaxLength(256);
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasMaxLength(256);
            b.Property(u => u.PendingEmail).HasMaxLength(256);
            b.Property(u => u.CreatedAtUtc).HasColumnType("timestamp with time zone");
            b.Property(u => u.Name).HasMaxLength(128);
            b.Property(u => u.Surname).HasMaxLength(128);
            b.Property(u => u.LifecycleStatus)
                .HasConversion<int>()
                .IsRequired();

            b.HasIndex(u => new { u.TenantId, u.NormalizedUserName })
                .HasDatabaseName("UserTenantNameIndex")
                .IsUnique();
            b.HasIndex(u => new { u.TenantId, u.NormalizedEmail })
                .HasDatabaseName("UserTenantEmailIndex")
                .IsUnique();
            b.HasIndex(u => new { u.TenantId, u.PhoneNumber })
                .HasDatabaseName("UserTenantPhoneIndex")
                .IsUnique();
            b.HasIndex(u => new { u.TenantId, u.LifecycleStatus });
            b.HasIndex(u => new { u.TenantId, u.IsPrimaryAdministrator })
                .HasDatabaseName("IX_AspNetUsers_TenantId_PrimaryAdministrator")
                .IsUnique()
                .HasFilter("\"IsPrimaryAdministrator\" = TRUE");
            // Per-user secret for HMAC-binding trusted-device fingerprints.
            // Nullable: null means no trusted devices ever registered for the user.
            b.Property(u => u.DeviceSecret).HasMaxLength(64);
        });

        builder.Entity<ApplicationRole>(b =>
        {
            b.ToTable("AspNetRoles");
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.TenantId);

            b.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(r => r.Name).HasMaxLength(256);
            b.Property(r => r.NormalizedName).HasMaxLength(256);

            b.HasIndex(r => r.NormalizedName)
                .HasDatabaseName("RoleNameIndex")
                .IsUnique()
                .HasFilter("\"TenantId\" IS NULL");

            b.HasIndex(r => new { r.TenantId, r.NormalizedName })
                .HasDatabaseName("RoleTenantNameIndex")
                .IsUnique()
                .HasFilter("\"TenantId\" IS NOT NULL");
        });

        builder.Entity<IdentityUserClaim<Guid>>(b =>
        {
            b.ToTable("AspNetUserClaims");
            b.HasKey(uc => uc.Id);
            b.HasIndex(uc => uc.UserId);
            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(uc => uc.UserId)
                .IsRequired();
        });

        builder.Entity<IdentityRoleClaim<Guid>>(b =>
        {
            b.ToTable("AspNetRoleClaims");
            b.HasKey(rc => rc.Id);
            b.HasIndex(rc => rc.RoleId);
            b.HasOne<ApplicationRole>()
                .WithMany()
                .HasForeignKey(rc => rc.RoleId)
                .IsRequired();
        });

        builder.Entity<IdentityUserRole<Guid>>(b =>
        {
            b.ToTable("AspNetUserRoles");
            b.HasKey(ur => new { ur.UserId, ur.RoleId });
            b.HasIndex(ur => ur.RoleId);
            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(ur => ur.UserId)
                .IsRequired();
            b.HasOne<ApplicationRole>()
                .WithMany()
                .HasForeignKey(ur => ur.RoleId)
                .IsRequired();
        });

        builder.Entity<IdentityUserLogin<Guid>>(b =>
        {
            b.ToTable("AspNetUserLogins");
            b.HasKey(l => new { l.LoginProvider, l.ProviderKey });
            b.HasIndex(l => l.UserId);
            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(l => l.UserId)
                .IsRequired();
        });

        builder.Entity<IdentityUserToken<Guid>>(b =>
        {
            b.ToTable("AspNetUserTokens");
            b.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });
            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .IsRequired();
        });
    }

    private static void ConfigureIdentityDomain(ModelBuilder builder)
    {
        builder.Entity<OrganizationUnit>(b =>
        {
            b.ToTable("OrganizationUnits");
            b.HasKey(ou => ou.Id);
            b.Property(ou => ou.Code).IsRequired().HasMaxLength(95);
            b.Property(ou => ou.DisplayName).IsRequired().HasMaxLength(128);
            b.HasMany(ou => ou.Roles).WithOne().HasForeignKey(our => our.OrganizationUnitId);
            b.HasMany(ou => ou.Users).WithOne().HasForeignKey(uou => uou.OrganizationUnitId);
        });

        builder.Entity<OrganizationUnitRole>(b =>
        {
            b.ToTable("OrganizationUnitRoles");
            b.HasKey(our => new { our.OrganizationUnitId, our.RoleId });
        });

        builder.Entity<UserOrganizationUnit>(b =>
        {
            b.ToTable("UserOrganizationUnits");
            b.HasKey(uou => new { uou.OrganizationUnitId, uou.UserId });
        });

        builder.Entity<IdentitySecurityLog>(b =>
        {
            b.ToTable("SecurityLogs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Action).HasMaxLength(64);
            b.Property(x => x.ApplicationName).HasMaxLength(256);
            b.Property(x => x.Identity).HasMaxLength(256);
            b.Property(x => x.UserName).HasMaxLength(256);
            b.Property(x => x.SessionId).HasMaxLength(64);
            b.Property(x => x.ClientId).HasMaxLength(64);
            b.Property(x => x.CorrelationId).HasMaxLength(64);
            b.Property(x => x.ClientIpAddress).HasMaxLength(64);
            b.Property(x => x.BrowserInfo).HasMaxLength(1024);
            b.Property(x => x.Location).HasMaxLength(128);
            b.Property(x => x.RiskLevel).HasMaxLength(32);
            b.Property(x => x.RiskAction).HasMaxLength(32);
            b.Property(x => x.CountryCode).HasMaxLength(16);
            b.HasIndex(x => new { x.CreationTime, x.RiskScore });
            b.HasIndex(x => new { x.RiskAction, x.MfaEnrolled, x.CreationTime });
            b.HasIndex(x => new { x.RiskReasonFlags, x.CreationTime });
            b.HasIndex(x => new { x.TenantId, x.UserId });
            b.HasIndex(x => new { x.TenantId, x.CreationTime, x.Id })
                .IsDescending(false, true, true);
            b.HasIndex(x => x.UserName);
            b.HasIndex(x => x.Action);
            b.HasIndex(x => x.CreationTime);
        });

        builder.Entity<IdentityLinkUser>(b =>
        {
            b.ToTable("LinkUsers");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.SourceUserId, x.SourceTenantId });
            b.HasIndex(x => new { x.TargetUserId, x.TargetTenantId });
        });

        builder.Entity<IdentityLinkUserApproval>(b =>
        {
            b.ToTable("LinkUserRequests");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasMaxLength(16).IsRequired();
            b.Property(x => x.RejectionReason).HasMaxLength(512);
            b.HasIndex(x => new { x.TenantId, x.RequestedByUserId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.TargetUserId, x.Status });
            b.HasIndex(x => x.CreatedAtUtc);
        });

        builder.Entity<IdentityUserDelegation>(b =>
        {
            b.ToTable("UserDelegations");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.SourceUserId });
            b.HasIndex(x => new { x.TenantId, x.TargetUserId });
        });

        builder.Entity<IdentityUserNotificationSecurityPreference>(b =>
        {
            b.ToTable("UserNotificationSecurityPreferences");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        });

        builder.Entity<IdentityUserLifecycleHistory>(b =>
        {
            b.ToTable("UserLifecycleHistory", table =>
            {
                table.HasCheckConstraint(
                    "CK_UserLifecycleHistory_CurrentStatus",
                    "\"CurrentStatus\" IN (0, 1, 2, 3, 4)");
                table.HasCheckConstraint(
                    "CK_UserLifecycleHistory_PreviousStatus",
                    "\"PreviousStatus\" IS NULL OR \"PreviousStatus\" IN (0, 1, 2, 3, 4)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.PreviousStatus).HasConversion<int?>();
            b.Property(x => x.CurrentStatus).HasConversion<int>().IsRequired();
            b.Property(x => x.ReasonCode).HasMaxLength(64).IsRequired();
            b.Property(x => x.Note).HasMaxLength(512);
            b.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.EffectiveAtUtc })
                .IsDescending(false, false, true);
        });

        builder.Entity<IdentityUserExportJob>(b =>
        {
            b.ToTable("UserExportJobs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasMaxLength(16).IsRequired();
            b.Property(x => x.Format).HasMaxLength(16).IsRequired();
            b.Property(x => x.FilterSnapshotJson).HasMaxLength(4096).IsRequired();
            b.Property(x => x.DownloadTokenHash).HasMaxLength(256).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(256);
            b.Property(x => x.ContentType).HasMaxLength(128);
            b.Property(x => x.FailureReason).HasMaxLength(512);
            b.HasIndex(x => new { x.TenantId, x.Status, x.NextAttemptTimeUtc });
            b.HasIndex(x => x.ExpireAtUtc);
        });

        builder.Entity<IdentityUserImportJob>(b =>
        {
            b.ToTable("UserImportJobs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasMaxLength(16).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(256);
            b.Property(x => x.ProtectedPayload).HasMaxLength(200000).IsRequired();
            b.Property(x => x.ErrorSnapshotJson).HasMaxLength(32768).IsRequired();
            b.Property(x => x.FailureReason).HasMaxLength(512);
            b.HasIndex(x => new { x.TenantId, x.Status, x.NextAttemptTimeUtc });
        });

        builder.Entity<IdentitySession>(b =>
        {
            b.ToTable("Sessions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.SessionId).HasMaxLength(64);
            b.Property(x => x.DeviceId).HasMaxLength(128);
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.Property(x => x.UserAgent).HasMaxLength(1024);
            b.Property(x => x.RevocationSource).HasMaxLength(64);
            b.Property(x => x.RevocationReason).HasMaxLength(512);
            b.Property(x => x.Location).HasMaxLength(128);
            b.Property(x => x.ClientId).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.UserId });
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasIndex(x => x.SessionId);
            b.HasIndex(x => x.CreationTime);
            b.HasIndex(x => x.LastActiveTime);
            b.HasIndex(x => x.RevokedAtUtc);
        });

        builder.Entity<IdentityLoginRiskOverride>(b =>
        {
            b.ToTable("LoginRiskOverrides");
            b.HasKey(x => x.Id);
            b.Property(x => x.ModeOverride).HasConversion<int>();
            b.Property(x => x.Reason).HasMaxLength(1024);
            b.HasIndex(x => new { x.TenantId, x.UserId, x.IsActive, x.ExpiresAtUtc });
        });

        builder.Entity<IdentityTrustedDevice>(b =>
        {
            b.ToTable("TrustedDevices");
            b.HasKey(x => x.Id);
            b.Property(x => x.DeviceHash).HasMaxLength(128).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.DeviceHash });
            b.HasIndex(x => x.ExpiresAtUtc);
        });

        builder.Entity<IdentityPasskeyCredential>(b =>
        {
            b.ToTable("PasskeyCredentials");
            b.HasKey(x => x.Id);
            b.Property(x => x.CredentialIdHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.CredentialId).IsRequired();
            b.Property(x => x.PublicKey).IsRequired();
            b.Property(x => x.UserHandle).IsRequired();
            b.Property(x => x.Transports).HasMaxLength(256);
            b.Property(x => x.FriendlyName).HasMaxLength(128).IsRequired();
            b.Property(x => x.ConcurrencyStamp).HasMaxLength(40).IsConcurrencyToken();
            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.HasIndex(x => x.CredentialIdHash).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.CreatedAtUtc });
        });

        builder.Entity<IdentityClaimType>(b =>
        {
            b.ToTable("ClaimTypes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(256);
        });

        builder.Entity<HostIdentitySecurityPolicy>(b =>
        {
            b.ToTable("HostIdentitySecurityPolicies");
            b.HasKey(x => x.Id);
            b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().HasMaxLength(40);
            b.HasIndex(x => x.TenantId);
        });
    }
}
