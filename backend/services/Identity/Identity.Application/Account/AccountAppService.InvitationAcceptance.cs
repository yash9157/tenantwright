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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [DisableAuditing]
    public async Task<PublicInvitationAcceptResultDto> AcceptOwnershipInvitationAsync(OwnershipInvitationAcceptInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var token = NormalizeRequiredInput(input.Token, 2048);
        var userId = _currentUser?.Id;
        if (!userId.HasValue) return InvalidInvitationAcceptanceResult("Authentication is required.");
        var invitation = await FindActiveInvitationAsync(token);
        if (invitation?.OwnershipRequestId is not Guid requestId) return InvalidInvitationAcceptanceResult("Invitation link is invalid or expired.");
        var user = await _userManager.Users.FirstOrDefaultAsync(x => x.Id == userId.Value && x.TenantId == invitation.TenantId);
        if (user == null || !user.EmailConfirmed || !string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
            return InvalidInvitationAcceptanceResult("Invitation details are invalid.");
        if (!await _userManager.IsInRoleAsync(user, TenantWrightIdentityRoleNames.TenantAdmin))
        {
            var roleResult = await _userManager.AddToRoleAsync(user, TenantWrightIdentityRoleNames.TenantAdmin);
            if (!roleResult.Succeeded) return InvalidInvitationAcceptanceResult("Invitation role assignment failed.");
        }
        if (!await TryClaimInvitationAsync(invitation, user.Id, DateTime.UtcNow))
            return new PublicInvitationAcceptResultDto { Succeeded = false, IsUsed = true, TenantId = invitation.TenantId, TenantName = invitation.TenantName, Email = invitation.Email, Message = "This invitation has already been used." };
        await unitOfWorkDbContext.SaveChangesAsync();
        if (_eventBus != null)
            await _eventBus.PublishEventAsync(new TenantOwnershipInvitationVerifiedEto { RequestId = requestId, TenantId = invitation.TenantId, UserId = user.Id, OccurredAtUtc = DateTime.UtcNow, UserName = user.UserName, DisplayName = ComposeOwnerDisplayName(user.Name, user.Surname), Email = user.Email });
        return new PublicInvitationAcceptResultDto { Succeeded = true, TenantId = invitation.TenantId, TenantName = invitation.TenantName, Email = invitation.Email, UserName = user.UserName ?? string.Empty, Message = "Ownership invitation accepted." };
    }

    // Owner display name for the tenant ownership-verified event: "Name Surname"
    // when present, else null so the Tenant read model can fall back to user name.
    private static string? ComposeOwnerDisplayName(string? name, string? surname)
    {
        var composed = string.Join(' ', new[] { name, surname }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(composed) ? null : composed;
    }

    [DisableAuditing]
    public async Task<PublicInvitationValidateResultDto> ValidatePublicInvitationAsync(PublicInvitationValidateInput input)
    {
        var normalizedToken = NormalizeRequiredInput(input.Token, 2048);
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return InvalidInvitationValidationResult("Invitation link is invalid or expired.");
        }

        var invitation = await FindActiveInvitationAsync(normalizedToken);
        if (invitation == null)
        {
            return InvalidInvitationValidationResult("Invitation link is invalid or expired.");
        }

        if (invitation.AcceptedAtUtc.HasValue)
        {
            return new PublicInvitationValidateResultDto
            {
                Succeeded = false,
                IsUsed = true,
                TenantId = invitation.TenantId,
                TenantName = invitation.TenantName,
                Email = invitation.Email,
                RoleName = TenantWrightIdentityRoleNames.Normalize(invitation.RoleName),
                SuggestedUserName = invitation.SuggestedUserName,
                Message = "This invitation has already been used."
            };
        }

        if (invitation.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return new PublicInvitationValidateResultDto
            {
                Succeeded = false,
                IsExpired = true,
                TenantId = invitation.TenantId,
                TenantName = invitation.TenantName,
                Email = invitation.Email,
                RoleName = TenantWrightIdentityRoleNames.Normalize(invitation.RoleName),
                SuggestedUserName = invitation.SuggestedUserName,
                Message = "This invitation link has expired."
            };
        }

        return new PublicInvitationValidateResultDto
        {
            Succeeded = true,
            TenantId = invitation.TenantId,
            TenantName = invitation.TenantName,
            Email = invitation.Email,
            RoleName = TenantWrightIdentityRoleNames.Normalize(invitation.RoleName),
            SuggestedUserName = invitation.SuggestedUserName,
            Message = "Invitation is valid."
        };
    }

    [DisableAuditing]
    public async Task<PublicInvitationAcceptResultDto> AcceptPublicInvitationAsync(PublicInvitationAcceptInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input.Token = NormalizeRequiredInput(input.Token, 2048);
        input.Email = NormalizeRequiredInput(input.Email, 256);
        input.UserName = NormalizeRequiredInput(input.UserName, 256);
        input.PhoneNumber = NormalizeOptionalInput(input.PhoneNumber, 32);
        input.Password = NormalizeRequiredInput(input.Password, 256);
        input.ConfirmPassword = NormalizeRequiredInput(input.ConfirmPassword, 256);
        input.VerificationBaseUrl = NormalizeRequiredInput(input.VerificationBaseUrl, 256);
        input.ClientIpAddress = NormalizeOptionalInput(input.ClientIpAddress, 64);
        input.BrowserInfo = NormalizeOptionalInput(input.BrowserInfo, 1024);
        input.CorrelationId = NormalizeOptionalInput(input.CorrelationId, 128);

        var invitation = await FindActiveInvitationAsync(input.Token);
        if (invitation == null)
        {
            return InvalidInvitationAcceptanceResult("Invitation link is invalid or expired.");
        }

        var tenantName = invitation.TenantName;

        if (invitation.AcceptedAtUtc.HasValue)
        {
            return new PublicInvitationAcceptResultDto
            {
                Succeeded = false,
                IsUsed = true,
                TenantId = invitation.TenantId,
                TenantName = tenantName,
                Email = invitation.Email,
                Message = "This invitation has already been used."
            };
        }

        if (invitation.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return new PublicInvitationAcceptResultDto
            {
                Succeeded = false,
                IsExpired = true,
                TenantId = invitation.TenantId,
                TenantName = tenantName,
                Email = invitation.Email,
                Message = "This invitation link has expired."
            };
        }

        if (!string.Equals(invitation.Email, input.Email, StringComparison.OrdinalIgnoreCase))
        {
            return InvalidInvitationAcceptanceResult("Invitation details are invalid.");
        }

        var safeBaseUrl = NormalizeAndValidatePublicBaseUrl(input.VerificationBaseUrl, _emailVerificationOptions.Value.AllowedBaseUrls);
        if (string.IsNullOrWhiteSpace(safeBaseUrl))
        {
            return new PublicInvitationAcceptResultDto
            {
                Succeeded = false,
                TenantId = invitation.TenantId,
                TenantName = tenantName,
                Email = invitation.Email,
                Message = "Account activation could not be started."
            };
        }

        using var tenantScope = _currentTenant.Change(invitation.TenantId, tenantName);

        var activeUnitOfWork = unitOfWorkDbContext.UnitOfWorkManager.Current;
        var operationUserManager = activeUnitOfWork is null
            ? _userManager
            : activeUnitOfWork.ServiceProvider.GetRequiredService<ApplicationUserManager>();
        var operationRoleManager = activeUnitOfWork is null
            ? _roleManager
            : activeUnitOfWork.ServiceProvider.GetRequiredService<ApplicationRoleManager>();

        async Task<PublicInvitationAcceptResultDto> RollbackAndReturnAsync(
            PublicInvitationAcceptResultDto result)
        {
            if (activeUnitOfWork is not null)
            {
                await activeUnitOfWork.RollbackAsync(CancellationToken.None);
            }

            return result;
        }

        var normalizedInviteEmail = operationUserManager.NormalizeEmail(invitation.Email);
        var emailExists = await operationUserManager.Users
            .FirstOrDefaultAsync(x => x.TenantId == invitation.TenantId && x.NormalizedEmail == normalizedInviteEmail);
        if (emailExists != null)
        {
            return new PublicInvitationAcceptResultDto
            {
                Succeeded = false,
                TenantId = invitation.TenantId,
                TenantName = tenantName,
                Email = invitation.Email,
                Message = "An account for this invitation email already exists."
            };
        }

        var userNameExists = await operationUserManager.FindByNameAsync(input.UserName);
        if (userNameExists != null)
        {
            return new PublicInvitationAcceptResultDto
            {
                Succeeded = false,
                TenantId = invitation.TenantId,
                TenantName = tenantName,
                Email = invitation.Email,
                Message = "That username is already in use."
            };
        }

        var invitationRoleName = TenantWrightIdentityRoleNames.Normalize(invitation.RoleName);
        if (!string.IsNullOrWhiteSpace(invitationRoleName))
        {
            var normalizedInvitationRoleName = invitationRoleName.ToUpperInvariant();
            var roleExists = await operationRoleManager.Roles
                .AnyAsync(x => x.TenantId == invitation.TenantId && x.NormalizedName == normalizedInvitationRoleName);
            if (!roleExists)
            {
                return new PublicInvitationAcceptResultDto
                {
                    Succeeded = false,
                    TenantId = invitation.TenantId,
                    TenantName = tenantName,
                    Email = invitation.Email,
                    Message = "Invitation role assignment is invalid."
                };
            }
        }

        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            TenantId = invitation.TenantId,
            UserName = input.UserName.Trim(),
            Email = invitation.Email,
            PhoneNumber = input.PhoneNumber,
            IsActive = true,
            RequireEmailActivationBeforeLogin = true
        };
        user.InitializeLifecycle(isActive: true);

        var invitationClaimed = await TryClaimInvitationAsync(invitation, user.Id, DateTime.UtcNow);
        if (!invitationClaimed)
        {
            return new PublicInvitationAcceptResultDto
            {
                Succeeded = false,
                IsUsed = true,
                TenantId = invitation.TenantId,
                TenantName = tenantName,
                Email = invitation.Email,
                Message = "This invitation has already been used."
            };
        }

        var createResult = await operationUserManager.CreateAsync(user, input.Password);
        if (!createResult.Succeeded)
        {
            return await RollbackAndReturnAsync(new PublicInvitationAcceptResultDto
            {
                Succeeded = false,
                TenantId = invitation.TenantId,
                TenantName = tenantName,
                Email = invitation.Email,
                Message = "Account creation failed.",
                Errors = createResult.Errors.Select(x => x.Description).ToList()
            });
        }

        if (!string.IsNullOrWhiteSpace(invitationRoleName))
        {
            var roleResult = await operationUserManager.AddToRoleAsync(user, invitationRoleName);
            if (!roleResult.Succeeded)
            {
                return await RollbackAndReturnAsync(new PublicInvitationAcceptResultDto
                {
                    Succeeded = false,
                    TenantId = invitation.TenantId,
                    TenantName = tenantName,
                    Email = invitation.Email,
                    Message = "Invitation role assignment failed.",
                    Errors = roleResult.Errors.Select(x => x.Description).ToList()
                });
            }
        }

        var verificationRequest = await BuildEmailVerificationRequestAsync(
            user,
            invitation.TenantId,
            safeBaseUrl,
            _emailVerificationOptions.Value.TemplateName,
            operationUserManager);
        await AddNotificationToOutboxAsync(verificationRequest);

        await SaveSecurityLogAsync(
            identity: "Account",
            action: "PublicInvitationAccepted",
            sessionId: null,
            userName: user.UserName,
            userId: user.Id,
            tenantId: user.TenantId,
            clientId: null,
            clientIpAddress: input.ClientIpAddress,
            browserInfo: input.BrowserInfo,
            correlationId: input.CorrelationId,
            location: null);

        await unitOfWorkDbContext.SaveChangesAsync();

        async Task InvalidateCommittedUserCachesAsync()
        {
            try
            {
                await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
                await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
                await _cacheInvalidator.InvalidateAliasesAsync(
                    user.TenantId,
                    IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to invalidate committed identity caches after public invitation acceptance. TenantId={TenantId}, UserId={UserId}",
                    user.TenantId,
                    user.Id);
            }
        }

        if (activeUnitOfWork is null)
        {
            await InvalidateCommittedUserCachesAsync();
        }
        else
        {
            activeUnitOfWork.OnCompleted(InvalidateCommittedUserCachesAsync);
        }

        return new PublicInvitationAcceptResultDto
        {
            Succeeded = true,
            TenantId = invitation.TenantId,
            TenantName = tenantName,
            Email = invitation.Email,
            UserName = user.UserName,
            Message = "Account created successfully."
        };
    }

}
