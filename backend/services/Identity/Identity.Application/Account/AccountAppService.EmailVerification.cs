using System.Threading.Tasks;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Caching;
using Identity.Application.Contracts.Users;
using Identity.Application.ApplicationUsers;
using Identity.Application.Caching;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Shared.Modularity.DependencyInjection;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System;
using Shared.Caching.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Identity.Application.Users;
using System.Security.Cryptography;
using System.Text;
using Shared;
using Shared.Caching.Sessions;
using Microsoft.Extensions.Options;
using Identity.Application.Account.Risk;
using Identity.Application.Contracts.Account.Risk;
using Identity.Domain.Shared.Account;
using Identity.Domain.Shared.Account.Risk;
using Shared.EventContracts;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.AuditLogs.Auditing;
using Shared.MultiTenancy;
using Identity.Application.SecurityLogs;
using Shared.Domain.Entities;
using Shared.Events;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Account;

public partial class AccountAppService
{

    [Audited]
    public async Task RequestEmailVerificationAsync(EmailVerificationRequestInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        NormalizeEmailVerificationRequestInput(input);

        var options = _emailVerificationOptions.Value;
        if (!options.Enabled || input.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(input.LoginInput))
        {
            return;
        }

        var user = await FindUserInTenantAsync(input.TenantId, input.LoginInput);

        await SaveSecurityLogAsync(
            identity: "Account",
            action: "EmailVerificationRequested",
            sessionId: null,
            userName: user?.UserName ?? input.LoginInput,
            userId: user?.Id,
            tenantId: input.TenantId,
            clientId: null,
            clientIpAddress: input.ClientIpAddress,
            browserInfo: input.BrowserInfo,
            correlationId: input.CorrelationId,
            location: null);

        if (user == null || string.IsNullOrWhiteSpace(user.Email) || user.EmailConfirmed)
        {
            await unitOfWorkDbContext.SaveChangesAsync();
            return;
        }

        var safeBaseUrl = NormalizeAndValidatePublicBaseUrl(input.VerificationBaseUrl, options.AllowedBaseUrls);
        if (string.IsNullOrWhiteSpace(safeBaseUrl))
        {
            await unitOfWorkDbContext.SaveChangesAsync();
            return;
        }
        await QueueEmailVerificationAsync(user, input.TenantId, safeBaseUrl, options.TemplateName);

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [DisableAuditing]
    public async Task<EmailVerificationResultDto> ConfirmEmailVerificationAsync(EmailVerificationConfirmInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        NormalizeEmailVerificationConfirmInput(input);

        if (input.TenantId == Guid.Empty || input.UserId == Guid.Empty || string.IsNullOrWhiteSpace(input.Token))
        {
            return new EmailVerificationResultDto
            {
                Succeeded = false,
                Message = "Invalid email verification request."
            };
        }

        var user = await _userManager.Users.FirstOrDefaultAsync(x => x.Id == input.UserId && x.TenantId == input.TenantId);
        if (user == null)
        {
            return new EmailVerificationResultDto
            {
                Succeeded = false,
                Message = "Invalid email verification request."
            };
        }

        if (!string.IsNullOrWhiteSpace(user.PendingEmail))
        {
            return await ConfirmPendingEmailChangeAsync(user, input.Token);
        }

        if (user.EmailConfirmed)
        {
            return new EmailVerificationResultDto
            {
                Succeeded = true,
                Message = "Email is already verified."
            };
        }

        var token = DecodeResetToken(input.Token);
        var confirmResult = await _userManager.ConfirmEmailAsync(user, token);
        if (confirmResult.Succeeded && user.RequireEmailActivationBeforeLogin)
        {
            user.RequireEmailActivationBeforeLogin = false;
            await _userManager.UpdateAsync(user);
            await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
            await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
            await _sessionRevocationService.RevokeAllAsync(user.Id, user.TenantId, action: "EmailVerified");
        }

        await SaveSecurityLogAsync(
            identity: "Account",
            action: confirmResult.Succeeded ? "EmailVerificationConfirmed" : "EmailVerificationFailed",
            sessionId: null,
            userName: user.UserName,
            userId: user.Id,
            tenantId: user.TenantId,
            clientId: null,
            clientIpAddress: null,
            browserInfo: null,
            correlationId: null,
            location: null);

        await unitOfWorkDbContext.SaveChangesAsync();

        if (confirmResult.Succeeded)
        {
            var ownershipInvitation = await unitOfWorkDbContext.IdentityPublicInvitations
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.AcceptedUserId == user.Id && x.OwnershipRequestId.HasValue);
            if (ownershipInvitation?.OwnershipRequestId is Guid requestId)
            {
                if (_eventBus != null)
                {
                    await _eventBus.PublishEventAsync(new TenantOwnershipInvitationVerifiedEto
                    {
                        RequestId = requestId,
                        TenantId = user.TenantId,
                        UserId = user.Id,
                        OccurredAtUtc = DateTime.UtcNow,
                        UserName = user.UserName,
                        DisplayName = ComposeOwnerDisplayName(user.Name, user.Surname),
                        Email = user.Email
                    });
                }
            }
        }

        return new EmailVerificationResultDto
        {
            Succeeded = confirmResult.Succeeded,
            Message = confirmResult.Succeeded
                ? "Email verified successfully."
                : "Email verification token is invalid or expired."
        };
    }

    private async Task<EmailVerificationResultDto> ConfirmPendingEmailChangeAsync(ApplicationUser user, string rawToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var previousEmail = user.Email;
        var pendingEmail = user.PendingEmail!;
        var token = DecodeResetToken(rawToken);
        var confirmResult = await _userManager.ChangeEmailAsync(user, pendingEmail, token);
        if (confirmResult.Succeeded)
        {
            user.PendingEmail = null;
            user.PendingEmailRequestedAtUtc = null;
            user.RequireEmailActivationBeforeLogin = false;
            user.EmailConfirmed = true;
            await _userManager.UpdateAsync(user);

            var aliasHashes = IdentityUserCacheContracts.BuildAliasHashes(user.UserName, previousEmail, pendingEmail, user.PhoneNumber);
            await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
            await _cacheInvalidator.InvalidateAliasesAsync(user.TenantId, aliasHashes);
            await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
            await _sessionRevocationService.RevokeAllAsync(user.Id, user.TenantId, action: "EmailChanged");
        }

        await SaveSecurityLogAsync(
            identity: "Account",
            action: confirmResult.Succeeded ? "EmailChangeConfirmed" : "EmailChangeFailed",
            sessionId: null,
            userName: user.UserName,
            userId: user.Id,
            tenantId: user.TenantId,
            clientId: null,
            clientIpAddress: null,
            browserInfo: null,
            correlationId: null,
            location: null);

        await unitOfWorkDbContext.SaveChangesAsync();

        return new EmailVerificationResultDto
        {
            Succeeded = confirmResult.Succeeded,
            Message = confirmResult.Succeeded
                ? "Email changed successfully."
                : "Email change token is invalid or expired."
        };
    }

    [Audited]
    public async Task ResendEmailVerificationAsync(EmailVerificationRequestInput input)
    {
        await RequestEmailVerificationAsync(input);
    }
}
