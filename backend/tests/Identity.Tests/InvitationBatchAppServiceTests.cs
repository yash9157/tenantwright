using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Invitations;
using Identity.Application.Invitations;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Shared.EventContracts;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Shared.CurrentUser;
using Shared.MultiTenancy;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Behavioral tests for bulk invitations (Identity gap claim 24). The backend is
/// the authoritative validator: it re-checks every normalized row, reports one
/// error per rejected row, and creates only the valid rows (partial success).
/// Header mapping is a browser concern and is intentionally not modeled here.
/// </summary>
public sealed class InvitationBatchAppServiceTests
{
    private const string BaseUrl = "https://app.example.com";
    private const string TenantName = "Test Organization";
    private const string RoleReviewer = "Reviewer";

    [Fact]
    public async Task Validate_batch_reports_per_row_errors_and_writes_nothing()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Service.ValidateBatchAsync(new ValidateInvitationBatchInput
        {
            Items =
            [
                new InvitationImportItem { RowNumber = 1, Email = "good@x.com", RoleName = RoleReviewer },
                new InvitationImportItem { RowNumber = 2, Email = "not-an-email", RoleName = RoleReviewer },
                new InvitationImportItem { RowNumber = 3, Email = "norole@x.com", RoleName = "" },
                new InvitationImportItem { RowNumber = 4, Email = "ghost@x.com", RoleName = "DoesNotExist" }
            ]
        });

        Assert.Equal(4, result.TotalRowCount);
        Assert.Equal(1, result.ValidRowCount);
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.RowNumber == 2 && e.Field == nameof(InvitationImportItem.Email));
        Assert.Contains(result.Errors, e => e.RowNumber == 3 && e.Field == nameof(InvitationImportItem.RoleName));
        Assert.Contains(result.Errors, e => e.RowNumber == 4 && e.Field == nameof(InvitationImportItem.RoleName));

        Assert.False(await fixture.DbContext.IdentityPublicInvitations.AnyAsync());
        Assert.Empty(fixture.Publisher.Requests);
    }

    [Fact]
    public async Task Create_batch_creates_valid_rows_and_skips_the_rest()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Service.CreateBatchAsync(new CreateInvitationBatchInput
        {
            AcceptanceBaseUrl = BaseUrl,
            Items =
            [
                new InvitationImportItem { RowNumber = 1, Email = "alice@x.com", RoleName = RoleReviewer },
                new InvitationImportItem { RowNumber = 2, Email = "bob@x.com", RoleName = RoleReviewer },
                new InvitationImportItem { RowNumber = 3, Email = "ALICE@x.com", RoleName = RoleReviewer }, // in-batch dup
                new InvitationImportItem { RowNumber = 4, Email = "bad", RoleName = RoleReviewer },          // malformed
                new InvitationImportItem { RowNumber = 5, Email = "carol@x.com", RoleName = "Nope" }      // unknown role
            ]
        });

        Assert.Equal(5, result.RequestedCount);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(3, result.SkippedCount);
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.RowNumber == 3 && e.Field == nameof(InvitationImportItem.Email));
        Assert.Contains(result.Errors, e => e.RowNumber == 4 && e.Field == nameof(InvitationImportItem.Email));
        Assert.Contains(result.Errors, e => e.RowNumber == 5 && e.Field == nameof(InvitationImportItem.RoleName));

        var stored = await fixture.DbContext.IdentityPublicInvitations.IgnoreQueryFilters()
            .Where(x => x.TenantId == fixture.TenantId)
            .Select(x => x.Email)
            .ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains("alice@x.com", stored);
        Assert.Contains("bob@x.com", stored);
        Assert.Equal(2, fixture.Publisher.Requests.Count);
    }

    [Fact]
    public async Task Create_batch_skips_existing_pending_invitation_and_existing_user()
    {
        var fixture = await CreateFixtureAsync();

        // Existing active pending invitation for pending@x.com.
        fixture.DbContext.IdentityPublicInvitations.Add(new IdentityPublicInvitation
        {
            Id = Guid.NewGuid(),
            TenantId = fixture.TenantId,
            TenantName = TenantName,
            Email = "pending@x.com",
            TokenHash = "hash",
            RoleName = RoleReviewer,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(3)
        });
        await fixture.DbContext.SaveChangesAsync();

        // Existing active user for existing@x.com.
        var existing = new ApplicationUser { UserName = "existing", Email = "existing@x.com", TenantId = fixture.TenantId, IsActive = true };
        Assert.True((await fixture.UserManager.CreateAsync(existing, "Pass123$")).Succeeded);

        var result = await fixture.Service.CreateBatchAsync(new CreateInvitationBatchInput
        {
            AcceptanceBaseUrl = BaseUrl,
            Items =
            [
                new InvitationImportItem { RowNumber = 1, Email = "pending@x.com", RoleName = RoleReviewer },
                new InvitationImportItem { RowNumber = 2, Email = "existing@x.com", RoleName = RoleReviewer },
                new InvitationImportItem { RowNumber = 3, Email = "fresh@x.com", RoleName = RoleReviewer }
            ]
        });

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Contains(result.Errors, e => e.RowNumber == 1);
        Assert.Contains(result.Errors, e => e.RowNumber == 2);
        Assert.Single(result.Created);
        Assert.Equal("fresh@x.com", result.Created[0].Email);
    }

    [Fact]
    public async Task Create_batch_rejects_reserved_role_per_row_for_tenant_admin()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Service.CreateBatchAsync(new CreateInvitationBatchInput
        {
            AcceptanceBaseUrl = BaseUrl,
            Items =
            [
                new InvitationImportItem { RowNumber = 1, Email = "ok@x.com", RoleName = RoleReviewer },
                new InvitationImportItem { RowNumber = 2, Email = "escalate@x.com", RoleName = TenantWrightIdentityRoleNames.TenantAdmin }
            ]
        });

        Assert.Equal(1, result.CreatedCount);
        Assert.Contains(result.Errors, e => e.RowNumber == 2 && e.Field == nameof(InvitationImportItem.RoleName));
    }

    [Fact]
    public async Task Batch_rejects_empty_items()
    {
        var fixture = await CreateFixtureAsync();

        await Assert.ThrowsAsync<SharedValidationException>(() =>
            fixture.Service.ValidateBatchAsync(new ValidateInvitationBatchInput { Items = [] }));
        await Assert.ThrowsAsync<SharedValidationException>(() =>
            fixture.Service.CreateBatchAsync(new CreateInvitationBatchInput { AcceptanceBaseUrl = BaseUrl, Items = [] }));
    }

    [Fact]
    public async Task Batch_rejects_request_over_configured_cap()
    {
        var fixture = await CreateFixtureAsync(maxBatchSize: 2);

        var input = new ValidateInvitationBatchInput
        {
            Items =
            [
                new InvitationImportItem { Email = "a@x.com", RoleName = RoleReviewer },
                new InvitationImportItem { Email = "b@x.com", RoleName = RoleReviewer },
                new InvitationImportItem { Email = "c@x.com", RoleName = RoleReviewer }
            ]
        };

        await Assert.ThrowsAsync<SharedValidationException>(() => fixture.Service.ValidateBatchAsync(input));
    }

    [Fact]
    public async Task Create_batch_rejects_cross_tenant_target_for_tenant_admin()
    {
        var fixture = await CreateFixtureAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.CreateBatchAsync(new CreateInvitationBatchInput
            {
                TenantId = Guid.NewGuid(), // a tenant other than the caller's
                AcceptanceBaseUrl = BaseUrl,
                Items = [new InvitationImportItem { Email = "x@x.com", RoleName = RoleReviewer }]
            }));
    }

    [Fact]
    public async Task Ownership_invitation_should_publish_created_ack_and_replay_same_invitation()
    {
        var fixture = await CreateFixtureAsync();
        var requestId = Guid.NewGuid();
        var input = new TenantOwnershipInvitationRequestedEto
        {
            RequestId = requestId,
            TenantId = fixture.TenantId,
            TenantName = TenantName,
            TargetEmail = "owner@example.test",
            Kind = TenantOwnershipRequestKindEto.InitialOwner,
            OccurredAtUtc = DateTime.UtcNow
        };

        using (fixture.CurrentTenant.Change(null))
        {
            await fixture.Service.CreateOwnershipInvitationAsync(input);
            await fixture.Service.CreateOwnershipInvitationAsync(input);
        }

        var invitation = await fixture.DbContext.IdentityPublicInvitations.IgnoreQueryFilters()
            .SingleAsync(x => x.OwnershipRequestId == requestId);
        var acknowledgements = fixture.EventBus.Published
            .Where(x => x.EventData is TenantOwnershipInvitationCreatedEto)
            .ToList();
        Assert.Equal(2, acknowledgements.Count);
        Assert.All(acknowledgements, record =>
        {
            var acknowledgement = Assert.IsType<TenantOwnershipInvitationCreatedEto>(record.EventData);
            Assert.Equal(requestId, acknowledgement.RequestId);
            Assert.Equal(fixture.TenantId, acknowledgement.TenantId);
            Assert.Equal(invitation.Id, acknowledgement.InvitationId);
            Assert.True(record.OnUnitOfWorkComplete);
            Assert.True(record.UseOutbox);
        });
        Assert.Equal(
            [fixture.TenantId, fixture.TenantId, fixture.TenantId],
            fixture.EventBus.CurrentTenantIds);
        Assert.Null(Assert.Single(fixture.Publisher.Requests).TenantId);
    }

    [Fact]
    public async Task Ownership_transfer_should_not_publish_initial_provisioning_ack()
    {
        var fixture = await CreateFixtureAsync();

        using (fixture.CurrentTenant.Change(null))
        {
            await fixture.Service.CreateOwnershipInvitationAsync(new TenantOwnershipInvitationRequestedEto
            {
                RequestId = Guid.NewGuid(),
                TenantId = fixture.TenantId,
                TenantName = TenantName,
                TargetEmail = "next-owner@example.test",
                Kind = TenantOwnershipRequestKindEto.Transfer,
                OccurredAtUtc = DateTime.UtcNow
            });
        }

        Assert.DoesNotContain(fixture.EventBus.Published,
            x => x.EventData is TenantOwnershipInvitationCreatedEto);
        Assert.Single(fixture.Publisher.Requests);
        Assert.Equal([fixture.TenantId], fixture.EventBus.CurrentTenantIds);
    }

    [Fact]
    public async Task Ownership_invitation_resend_should_rotate_the_token_and_queue_a_second_delivery()
    {
        var fixture = await CreateFixtureAsync();
        var requestId = Guid.NewGuid();
        var input = new TenantOwnershipInvitationRequestedEto
        {
            RequestId = requestId,
            TenantId = fixture.TenantId,
            TenantName = TenantName,
            TargetEmail = "owner@example.test",
            Kind = TenantOwnershipRequestKindEto.InitialOwner,
            OccurredAtUtc = DateTime.UtcNow
        };
        using (fixture.CurrentTenant.Change(null))
        {
            await fixture.Service.CreateOwnershipInvitationAsync(input);
        }
        var before = await fixture.DbContext.IdentityPublicInvitations.IgnoreQueryFilters()
            .SingleAsync(x => x.OwnershipRequestId == requestId);
        var previousTokenHash = before.TokenHash;

        input.IsResend = true;
        using (fixture.CurrentTenant.Change(null))
        {
            await fixture.Service.CreateOwnershipInvitationAsync(input);
        }

        var after = await fixture.DbContext.IdentityPublicInvitations.IgnoreQueryFilters()
            .SingleAsync(x => x.OwnershipRequestId == requestId);
        Assert.Equal(before.Id, after.Id);
        Assert.NotEqual(previousTokenHash, after.TokenHash);
        Assert.Equal(2, fixture.Publisher.Requests.Count);
        Assert.Equal(
            [fixture.TenantId, fixture.TenantId, fixture.TenantId, fixture.TenantId],
            fixture.EventBus.CurrentTenantIds);
    }

    private static async Task<Fixture> CreateFixtureAsync(int maxBatchSize = 200)
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, TenantName);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), tenantId, "tenant.admin", TenantWrightIdentityRoleNames.TenantAdmin);

        var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        var eventBus = new IdentityTestSupport.CapturingMessageSender(currentTenant);

        var options = Options.Create(new PublicInvitationManagementOptions
        {
            Enabled = true,
            TokenTtlHours = 168,
            TemplateName = "Identity.PublicInvitationLink",
            Subject = "You are invited",
            AllowedBaseUrls = [BaseUrl],
            MaxBatchSize = maxBatchSize
        });

        var createRole = await roleManager.CreateAsync(new ApplicationRole(RoleReviewer) { TenantId = tenantId });
        Assert.True(createRole.Succeeded, string.Join(", ", createRole.Errors.Select(x => x.Description)));

        var service = new InvitationAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            roleManager,
            guard,
            currentUser,
            currentTenant,
            options,
            eventBus);

        return new Fixture(service, dbContext, userManager, eventBus, eventBus, currentTenant, tenantId);
    }

    private sealed record Fixture(
        InvitationAppService Service,
        IdentityDbContextApp DbContext,
        ApplicationUserManager UserManager,
        IdentityTestSupport.CapturingMessageSender Publisher,
        IdentityTestSupport.CapturingMessageSender EventBus,
        IdentityTestSupport.TestCurrentTenant CurrentTenant,
        Guid TenantId);
}
