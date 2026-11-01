using Identity.Application.Account;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account;
using Identity.Domain.ApplicationUsers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Shared.EventContracts;
using Xunit;

namespace Identity.Tests;

public sealed class ProfileEmailChangeTests
{
    private const string AllowedBaseUrl = "https://localhost:7239";
    private const string InitialPassword = "Initial#12345";

    [Fact]
    public async Task UpdateAsync_should_require_password_or_mfa_before_storing_pending_email()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = await CreateUserAsync(userManager, tenantId, "profile.email.proof", "profile.email.proof@example.test");
        var publisher = new IdentityTestSupport.CapturingMessageSender();
        var service = CreateService(dbContext, currentTenant, userManager, user, publisher);

        await Assert.ThrowsAsync<SharedValidationException>(() => service.UpdateAsync(new UpdateProfileDto
        {
            UserName = user.UserName!,
            Email = "pending@example.test",
            EmailConfirmationBaseUrl = AllowedBaseUrl,
            ConcurrencyStamp = user.ConcurrencyStamp
        }));

        var row = await dbContext.Users.SingleAsync(x => x.Id == user.Id);
        Assert.Equal("profile.email.proof@example.test", row.Email);
        Assert.Null(row.PendingEmail);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task UpdateAsync_should_store_pending_email_and_send_confirmation_without_changing_active_email()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = await CreateUserAsync(userManager, tenantId, "profile.email.pending", "profile.email.pending@example.test");
        var publisher = new IdentityTestSupport.CapturingMessageSender();
        var service = CreateService(dbContext, currentTenant, userManager, user, publisher);

        var result = await service.UpdateAsync(new UpdateProfileDto
        {
            UserName = user.UserName!,
            Email = "new.profile.email@example.test",
            EmailConfirmationBaseUrl = AllowedBaseUrl,
            CurrentPassword = InitialPassword,
            ConcurrencyStamp = user.ConcurrencyStamp
        });

        var row = await dbContext.Users.SingleAsync(x => x.Id == user.Id);
        Assert.Equal("profile.email.pending@example.test", row.Email);
        Assert.Equal("new.profile.email@example.test", row.PendingEmail);
        Assert.Equal("new.profile.email@example.test", result.PendingEmail);
        Assert.NotNull(row.PendingEmailRequestedAtUtc);

        var request = Assert.IsType<NotificationSendRequestedTenantEto>(Assert.Single(publisher.Published).EventData);
        Assert.Equal(tenantId, request.TenantId);
        Assert.Equal("Identity.EmailVerificationLink", request.TemplateName);
        Assert.Contains(request.Recipients, x => x.RecipientValue == "new.profile.email@example.test");
        Assert.StartsWith($"{AllowedBaseUrl}/auth/email-verification/confirm?", request.TemplateModel["link"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_should_reject_unapproved_confirmation_url_before_consuming_recovery_code()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = await CreateUserAsync(userManager, tenantId, "profile.email.mfa", "profile.email.mfa@example.test");
        user.TwoFactorEnabled = true;
        var recoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!.ToList();
        var recoveryCode = Assert.Single(recoveryCodes);
        var publisher = new IdentityTestSupport.CapturingMessageSender();
        var service = CreateService(dbContext, currentTenant, userManager, user, publisher);

        await Assert.ThrowsAsync<SharedValidationException>(() => service.UpdateAsync(new UpdateProfileDto
        {
            UserName = user.UserName!,
            Email = "mfa.new@example.test",
            EmailConfirmationBaseUrl = "https://evil.example.test",
            TwoFactorRecoveryCode = recoveryCode,
            ConcurrencyStamp = user.ConcurrencyStamp
        }));

        Assert.Equal(1, await userManager.CountRecoveryCodesAsync(user));
        Assert.Null((await dbContext.Users.SingleAsync(x => x.Id == user.Id)).PendingEmail);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task GenerateMfaSetupAsync_should_require_current_password_before_replacing_authenticator_key()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = await CreateUserAsync(userManager, tenantId, "profile.mfa.proof", "profile.mfa.proof@example.test");
        var publisher = new IdentityTestSupport.CapturingMessageSender();
        var service = CreateService(dbContext, currentTenant, userManager, user, publisher);

        await Assert.ThrowsAsync<SharedValidationException>(() => service.GenerateMfaSetupAsync(
            new MfaSensitiveOperationProofInput { CurrentPassword = "incorrect" }));

        var setup = await service.GenerateMfaSetupAsync(
            new MfaSensitiveOperationProofInput { CurrentPassword = InitialPassword });

        Assert.False(string.IsNullOrWhiteSpace(setup.SharedKey));
        Assert.False(string.IsNullOrWhiteSpace(setup.AuthenticatorUri));
    }

    [Fact]
    public async Task Pending_email_change_should_resend_with_server_owned_url_and_allow_cancellation()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = await CreateUserAsync(userManager, tenantId, "profile.pending.resend", "profile.pending.resend@example.test");
        user.PendingEmail = "replacement@example.test";
        user.PendingEmailRequestedAtUtc = DateTime.UtcNow;
        await userManager.UpdateAsync(user);
        var publisher = new IdentityTestSupport.CapturingMessageSender();
        var service = CreateService(dbContext, currentTenant, userManager, user, publisher);

        var resent = await service.ResendPendingEmailConfirmationAsync();
        var request = Assert.Single(publisher.Requests);
        var canceled = await service.CancelPendingEmailChangeAsync();

        Assert.Equal("replacement@example.test", resent.PendingEmail);
        Assert.StartsWith($"{AllowedBaseUrl}/auth/email-verification/confirm?", request.TemplateModel["link"], StringComparison.Ordinal);
        Assert.Null(canceled.PendingEmail);
        Assert.Null((await dbContext.Users.SingleAsync(x => x.Id == user.Id)).PendingEmail);
    }

    private static ProfileAppService CreateService(
        Identity.EntityFrameworkCore.EntityFrameworkCore.IdentityDbContextApp dbContext,
        IdentityTestSupport.TestCurrentTenant currentTenant,
        ApplicationUserManager userManager,
        ApplicationUser user,
        IdentityTestSupport.CapturingMessageSender publisher)
    {
        return new ProfileAppService(
            userManager,
            new IdentityTestSupport.TestCurrentUser(user.Id, currentTenant.Id, user.UserName),
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new HttpContextAccessor(),
            new IdentityTestSupport.NoopIdentitySessionRevocationService(),
            new IdentityUserSecurityAlertPublisher(publisher, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance),
            Options.Create(new EmailVerificationOptions
            {
                AllowedBaseUrls = [AllowedBaseUrl]
            }),
            publisher);
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        ApplicationUserManager userManager,
        Guid tenantId,
        string userName,
        string email)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        var result = await userManager.CreateAsync(user, InitialPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
        return user;
    }
}
