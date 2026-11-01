using Identity.Application.Account;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Invitations;
using Identity.Application.Contracts.Users;
using Identity.Application.Invitations;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Xunit;

namespace Identity.Tests;

public class Phase12IdentityPreferenceAndImportExportTests
{
    [Fact]
    public async Task Profile_preferences_should_default_enabled_and_persist_updates()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUser(tenantId, "profile.user", "profile.user@example.com");

        await using var dbContext = IdentityTestSupport.CreateDbContext(new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var userManager = IdentityTestSupport.CreateUserManager(dbContext, new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));
        var notificationPublisher = new IdentityTestSupport.CapturingMessageSender();
        var service = new ProfileAppService(
            userManager,
            new IdentityTestSupport.TestCurrentUser(user.Id, tenantId, user.UserName),
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new HttpContextAccessor(),
            new IdentityTestSupport.NoopIdentitySessionRevocationService(),
            new IdentityUserSecurityAlertPublisher(notificationPublisher, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance),
            Options.Create(new EmailVerificationOptions
            {
                AllowedBaseUrls = ["https://localhost:7239"]
            }),
            notificationPublisher);

        var defaults = await service.GetPreferencesAsync();

        Assert.True(defaults.Channels.EmailEnabled);
        Assert.True(defaults.SecurityAlerts.NewDeviceSignInAlertEnabled);
        Assert.True(defaults.SecurityAlerts.RiskBlockedAlertEnabled);
        Assert.True(defaults.SecurityAlerts.AdminSessionRevokedAlertEnabled);
        Assert.True(defaults.SecurityAlerts.MfaChangedAlertEnabled);

        var updated = await service.UpdatePreferencesAsync(new UpdateUserNotificationSecurityPreferencesInput
        {
            Channels = new NotificationChannelPreferencesDto
            {
                EmailEnabled = false
            },
            SecurityAlerts = new SecurityAlertPreferencesDto
            {
                NewDeviceSignInAlertEnabled = false,
                RiskBlockedAlertEnabled = true,
                AdminSessionRevokedAlertEnabled = false,
                MfaChangedAlertEnabled = true
            }
        });

        Assert.False(updated.Channels.EmailEnabled);
        Assert.False(updated.SecurityAlerts.NewDeviceSignInAlertEnabled);
        Assert.True(updated.SecurityAlerts.RiskBlockedAlertEnabled);
        Assert.False(updated.SecurityAlerts.AdminSessionRevokedAlertEnabled);
        Assert.True(updated.SecurityAlerts.MfaChangedAlertEnabled);

        var row = await dbContext.IdentityUserNotificationSecurityPreferences.SingleAsync();
        Assert.Equal(user.Id, row.UserId);
        Assert.False(row.EmailEnabled);
        Assert.False(row.NewDeviceSignInAlertEnabled);
        Assert.False(row.AdminSessionRevokedAlertEnabled);
    }

    [Fact]
    public async Task Internal_preference_lookup_should_be_tenant_safe_and_default_enabled()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var tenantUser = CreateUser(tenantId, "tenant.user", "tenant.user@example.com");
        var otherUser = CreateUser(otherTenantId, "other.user", "other.user@example.com");

        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        dbContext.Users.Add(tenantUser);
        await dbContext.SaveChangesAsync();
        using (currentTenant.Change(otherTenantId, "tenant-b"))
        {
            dbContext.Users.Add(otherUser);
            await dbContext.SaveChangesAsync();
        }

        var service = new InternalProfilePreferenceLookupAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentTenant);

        var result = await service.GetAlertPreferenceAsync(
            tenantUser.Id,
            Identity.Domain.Shared.ApplicationUsers.IdentityUserNotificationAlertKeys.RiskBlocked);

        Assert.True(result.Enabled);

        await Assert.ThrowsAsync<SharedValidationException>(() => service.GetAlertPreferenceAsync(
            otherUser.Id,
            Identity.Domain.Shared.ApplicationUsers.IdentityUserNotificationAlertKeys.RiskBlocked));
    }

    [Fact]
    public async Task ValidateImportAsync_should_report_invalid_rows_without_mutating_users()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = IdentityTestSupport.CreateDbContext(new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a"));

        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        var currentUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenantId, "tenant-admin", TenantWrightIdentityRoleNames.TenantAdmin);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        var coordinator = CreateImportCoordinator(dbContext, userManager, guard, currentUser, currentTenant);

        var validation = await coordinator.ValidateImportAsync(new ValidateUserImportInput
        {
            FileName = "users.csv",
            CsvContent = string.Join(Environment.NewLine,
                "UserName,Email,Password,IsActive,RoleNames",
                "dup.user,dup.user@example.com,Temp#123A,true,",
                "dup.user,not-an-email,,banana,")
        });

        Assert.Equal(2, validation.TotalRowCount);
        Assert.Equal(1, validation.ValidRowCount);
        Assert.Contains(validation.Errors, x => x.Message.Contains("Duplicate UserName", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, x => x.Message.Contains("Email is invalid", StringComparison.Ordinal));
        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task StartImportAsync_and_export_processing_should_create_and_export_tenant_users()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        var currentUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenantId, "tenant-admin", TenantWrightIdentityRoleNames.TenantAdmin);

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        dbContext.Roles.Add(new ApplicationRole("Reviewer")
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NormalizedName = "REVIEWER",
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });
        await dbContext.SaveChangesAsync();

        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        var payloadProtector = new TestImportPayloadProtector();
        var backgroundJobs = new IdentityTestSupport.CapturingBackgroundJobManager();
        var coordinator = new IdentityUserImportExportCoordinator(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            guard,
            currentUser,
            currentTenant,
            payloadProtector,
            backgroundJobs);

        var importJob = await coordinator.StartImportAsync(new StartUserImportInput
        {
            FileName = "users.csv",
            CsvContent = string.Join(Environment.NewLine,
                "UserName,Email,Password,PhoneNumber,IsActive,RoleNames",
                "alice,alice@example.com,Temp#123A,+15550001,true,Reviewer",
                "bob,bob@example.com,Temp#123A,+15550002,false,")
        });

        Assert.Equal("Queued", importJob.Status);
        Assert.Equal(0, importJob.CreatedCount);
        Assert.Empty(await dbContext.Users.ToListAsync());
        var importEnqueue = Assert.Single(backgroundJobs.Jobs);
        Assert.Equal(typeof(IdentityUserImportJobArgs), importEnqueue.ArgsType);
        Assert.Equal(importJob.Id, ((IdentityUserImportJobArgs)importEnqueue.Args).JobId);
        Assert.Equal("identity-default", importEnqueue.Options?.Queue);

        var importProcessor = new IdentityUserImportProcessor(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            userManager,
            currentTenant,
            payloadProtector);
        Assert.Equal(1, await importProcessor.ProcessBatchAsync());

        var completedImport = await coordinator.GetImportAsync(importJob.Id);
        Assert.Equal("Completed", completedImport.Status);
        Assert.Equal(2, completedImport.CreatedCount);
        Assert.Equal(2, await dbContext.Users.CountAsync());

        var exportJob = await coordinator.CreateExportAsync(new RequestUserExportInput
        {
            Format = "csv",
            MaxRowCount = 10
        });
        Assert.Equal(2, backgroundJobs.Jobs.Count);
        var exportEnqueue = backgroundJobs.Jobs[1];
        Assert.Equal(typeof(IdentityUserExportJobArgs), exportEnqueue.ArgsType);
        Assert.Equal(exportJob.Id, ((IdentityUserExportJobArgs)exportEnqueue.Args).JobId);
        Assert.Equal("identity-default", exportEnqueue.Options?.Queue);

        var processor = new IdentityUserExportProcessor(IdentityTestSupport.AsDbContextProvider(dbContext), currentTenant);
        Assert.Equal(1, await processor.ProcessBatchAsync());

        var completedJob = await coordinator.GetExportAsync(exportJob.Id);
        Assert.True(completedJob.IsReadyForDownload);
        Assert.NotNull(exportJob.DownloadToken);

        var download = await coordinator.GetExportDownloadAsync(exportJob.Id, exportJob.DownloadToken!);
        var csv = System.Text.Encoding.UTF8.GetString(download.Content);

        Assert.Contains("UserName,Email,PhoneNumber,IsActive,RoleNames", csv, StringComparison.Ordinal);
        Assert.Contains("alice@example.com", csv, StringComparison.Ordinal);
        Assert.Contains("Reviewer", csv, StringComparison.Ordinal);
        Assert.Contains("bob@example.com", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartImportAsync_should_reject_files_over_the_row_cap_without_creating_a_job()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        var currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(),
            tenantId,
            "tenant-admin",
            TenantWrightIdentityRoleNames.TenantAdmin);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        var coordinator = CreateImportCoordinator(dbContext, userManager, guard, currentUser, currentTenant);
        var rows = Enumerable.Range(1, 1001)
            .Select(index => $"user{index},user{index}@example.com,Temp#123A");

        await Assert.ThrowsAsync<SharedValidationException>(() =>
            coordinator.StartImportAsync(new StartUserImportInput
            {
                FileName = "too-many.csv",
                CsvContent = string.Join(
                    Environment.NewLine,
                    new[] { "UserName,Email,Password" }.Concat(rows))
            }));

        Assert.Empty(await dbContext.IdentityUserImportJobs.ToListAsync());
        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task Import_processor_should_resume_after_the_last_committed_row()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        var currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(),
            tenantId,
            "tenant-admin",
            TenantWrightIdentityRoleNames.TenantAdmin);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var firstUser = CreateUser(tenantId, "first", "first@example.com");
        dbContext.Users.Add(firstUser);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        var protector = new TestImportPayloadProtector();
        var payload = new
        {
            fileName = "resume.csv",
            rows = new[]
            {
                new
                {
                    rowNumber = 2,
                    userName = "first",
                    email = "first@example.com",
                    password = "Temp#123A",
                    phoneNumber = (string?)null,
                    isActive = true,
                    roleNames = Array.Empty<string>()
                },
                new
                {
                    rowNumber = 3,
                    userName = "second",
                    email = "second@example.com",
                    password = "Temp#123A",
                    phoneNumber = (string?)null,
                    isActive = true,
                    roleNames = Array.Empty<string>()
                }
            }
        };
        var job = new IdentityUserImportJob
        {
            TenantId = tenantId,
            RequestedByUserId = currentUser.Id!.Value,
            Status = IdentityUserJobStatuses.Queued,
            FileName = "resume.csv",
            ProtectedPayload = protector.Protect(System.Text.Json.JsonSerializer.Serialize(payload)),
            TotalRowCount = 2,
            ValidRowCount = 2,
            CreatedCount = 1,
            RequestedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            NextAttemptTimeUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        dbContext.IdentityUserImportJobs.Add(job);
        await dbContext.SaveChangesAsync();

        var processor = new IdentityUserImportProcessor(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            userManager,
            currentTenant,
            protector);

        Assert.Equal(1, await processor.ProcessBatchAsync());
        Assert.Equal(2, await dbContext.Users.CountAsync());
        Assert.Equal("Completed", job.Status);
        Assert.Equal(2, job.CreatedCount);
        Assert.NotNull(await dbContext.Users.SingleOrDefaultAsync(user => user.UserName == "second"));
    }

    [Fact]
    public async Task Bulk_activate_should_fail_whole_request_when_any_user_is_out_of_scope()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        var currentUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenantId, "tenant-admin", TenantWrightIdentityRoleNames.TenantAdmin);

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var inScopeUser = CreateUser(tenantId, "scope.user", "scope.user@example.com", isActive: false);
        var outOfScopeUser = CreateUser(otherTenantId, "other.user", "other.user@example.com", isActive: false);
        dbContext.Users.Add(inScopeUser);
        await dbContext.SaveChangesAsync();
        using (currentTenant.Change(otherTenantId, "tenant-b"))
        {
            dbContext.Users.Add(outOfScopeUser);
            await dbContext.SaveChangesAsync();
        }

        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        var coordinator = CreateImportCoordinator(dbContext, userManager, guard, currentUser, currentTenant);
        var service = new UserAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentTenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            new IdentityTestSupport.NoopIdentitySessionRevocationService(),
            guard,
            currentUser,
            coordinator,
            new IdentityTestSupport.EmptyTenantStore());

        await Assert.ThrowsAsync<SharedValidationException>(() => service.ActivateBulkAsync(new BulkUserActionInput
        {
            UserIds =
            [
                inScopeUser.Id.ToString("D"),
                outOfScopeUser.Id.ToString("D")
            ]
        }));

        Assert.False((await dbContext.Users.SingleAsync(x => x.Id == inScopeUser.Id)).IsActive);
    }

    [Fact]
    public async Task Host_admin_paged_users_should_honor_explicit_tenant_filter()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        var currentUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host-admin", TenantWrightIdentityRoleNames.HostAdmin);

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        dbContext.Users.AddRange(
            CreateUser(tenantId, "tenant.user", "tenant.user@example.com"),
            CreateUser(otherTenantId, "other.user", "other.user@example.com"));
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(dbContext, currentTenant, currentUser);

        var result = await service.GetPagedListAsync(new GetUsersInput
        {
            TenantId = tenantId,
            MaxResultCount = 10
        });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("tenant.user", Assert.Single(result.Items).UserName);
    }

    [Fact]
    public async Task Host_admin_create_user_should_store_explicit_tenant_id()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        var currentUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host-admin", TenantWrightIdentityRoleNames.HostAdmin);

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var service = CreateUserService(dbContext, currentTenant, currentUser);

        var created = await service.CreateAsync(new UserCreateDto
        {
            TenantId = tenantId,
            UserName = "host.created",
            Email = "host.created@example.com",
            Password = "Temp#123A",
            IsActive = true,
            RoleNames = []
        });

        Assert.Equal(tenantId, created.TenantId);
        Assert.Equal(
            tenantId,
            (await dbContext.Users.IgnoreQueryFilters().SingleAsync(x => x.UserName == "host.created")).TenantId);
    }

    [Fact]
    public async Task Tenant_admin_should_not_resolve_a_different_tenant_id()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        var currentUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenantId, "tenant-admin", TenantWrightIdentityRoleNames.TenantAdmin);

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.FromResult(guard.ResolveTenantId(otherTenantId)));
    }

    [Fact]
    public async Task Host_admin_invitation_list_should_honor_explicit_tenant_filter()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        var currentUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host-admin", TenantWrightIdentityRoleNames.HostAdmin);
        var now = DateTime.UtcNow;

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        dbContext.IdentityPublicInvitations.AddRange(
            CreateInvitation(tenantId, "tenant.invite@example.com", now.AddDays(2)),
            CreateInvitation(otherTenantId, "other.invite@example.com", now.AddDays(2)));
        await dbContext.SaveChangesAsync();

        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        var service = new InvitationAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            roleManager,
            guard,
            currentUser,
            currentTenant,
            Options.Create(new PublicInvitationManagementOptions()),
            IdentityTestSupport.CreateNoopMessageSender());

        var result = await service.GetListAsync(new InvitationListInput
        {
            TenantId = tenantId,
            MaxResultCount = 10
        });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("tenant.invite@example.com", Assert.Single(result.Items).Email);
    }

    private static ApplicationUser CreateUser(Guid tenantId, string userName, string email, bool isActive = true)
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsActive = isActive,
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
    }

    private static IdentityPublicInvitation CreateInvitation(Guid tenantId, string email, DateTime expiresAtUtc)
    {
        return new IdentityPublicInvitation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TenantName = "Tenant",
            Email = email,
            TokenHash = Guid.NewGuid().ToString("N"),
            RoleName = "Reviewer",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
            ExpiresAtUtc = expiresAtUtc
        };
    }

    private static UserAppService CreateUserService(
        IdentityDbContextApp dbContext,
        IdentityTestSupport.TestCurrentTenant currentTenant,
        IdentityTestSupport.TestCurrentUser currentUser)
    {
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        var coordinator = CreateImportCoordinator(dbContext, userManager, guard, currentUser, currentTenant);

        return new UserAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentTenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            new IdentityTestSupport.NoopIdentitySessionRevocationService(),
            guard,
            currentUser,
            coordinator,
            new IdentityTestSupport.EmptyTenantStore());
    }

    private static IdentityUserImportExportCoordinator CreateImportCoordinator(
        IdentityDbContextApp dbContext,
        ApplicationUserManager userManager,
        TenantUserManagementGuard guard,
        IdentityTestSupport.TestCurrentUser currentUser,
        IdentityTestSupport.TestCurrentTenant currentTenant)
    {
        return new IdentityUserImportExportCoordinator(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            guard,
            currentUser,
            currentTenant,
            new TestImportPayloadProtector(),
            new IdentityTestSupport.CapturingBackgroundJobManager());
    }

    private sealed class TestImportPayloadProtector : IIdentityUserImportPayloadProtector
    {
        private const string Prefix = "protected:";

        public string Protect(string plaintext)
        {
            return Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        }

        public string Unprotect(string protectedPayload)
        {
            return System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedPayload[Prefix.Length..]));
        }
    }
}
