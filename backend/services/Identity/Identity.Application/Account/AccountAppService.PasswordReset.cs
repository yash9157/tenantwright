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
    public async Task RequestPasswordResetAsync(PasswordResetRequestInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        NormalizePasswordResetRequestInput(input);

        var options = _passwordResetOptions.Value;
        if (!options.Enabled || input.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(input.LoginInput))
        {
            return;
        }

        var throttled = await TryConsumePasswordResetQuotaAsync(
            input.TenantId,
            input.LoginInput,
            input.ClientIpAddress,
            options,
            CancellationToken.None);
        if (!throttled)
        {
            return;
        }

        var user = await FindUserInTenantAsync(input.TenantId, input.LoginInput);

        await SaveSecurityLogAsync(
            identity: "Account",
            action: "PasswordResetRequested",
            sessionId: null,
            userName: user?.UserName ?? input.LoginInput,
            userId: user?.Id,
            tenantId: input.TenantId,
            clientId: null,
            clientIpAddress: input.ClientIpAddress,
            browserInfo: input.BrowserInfo,
            correlationId: input.CorrelationId,
            location: null);

        LoginRiskMetrics.RecordPasswordResetRequested();

        if (user == null || string.IsNullOrWhiteSpace(user.Email))
        {
            await unitOfWorkDbContext.SaveChangesAsync();
            return;
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var safeBaseUrl = NormalizeAndValidatePublicBaseUrl(input.ResetBaseUrl, options.AllowedBaseUrls);
        if (string.IsNullOrWhiteSpace(safeBaseUrl))
        {
            await unitOfWorkDbContext.SaveChangesAsync();
            return;
        }

        var link =
            $"{safeBaseUrl}{ResetPasswordPublicPath}?tenantId={input.TenantId:D}&userId={user.Id:D}&token={Uri.EscapeDataString(encodedToken)}";

        var request = new NotificationSendRequestedTenantEto
        {
            RequestId = Guid.CreateVersion7(),
            TenantId = input.TenantId,
            Channel = NotificationSendChannel.Email,
            Subject = "Reset your password",
            TemplateName = options.TemplateName,
            TemplateModel = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["link"] = link,
                ["userId"] = user.Id.ToString("D"),
                ["userName"] = user.UserName
            },
            Recipients =
            [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = user.Email
                }
            ],
            OccurredAtUtc = DateTime.UtcNow,
            SourceService = "identity-service"
        };

        var eventBus = _eventBus ?? throw new InvalidOperationException("TenantWright message sender is required to publish password reset notifications.");
        await eventBus.SendCommandAsync(request);

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    public async Task<PasswordResetValidateResultDto> ValidatePasswordResetAsync(PasswordResetValidateInput input)
    {
        NormalizePasswordResetValidateInput(input);

        if (input.TenantId == Guid.Empty || input.UserId == Guid.Empty || string.IsNullOrWhiteSpace(input.Token))
        {
            return new PasswordResetValidateResultDto
            {
                Status = PasswordResetValidationStatus.Invalid,
                Message = "Invalid password reset token."
            };
        }

        var user = await _userManager.Users.FirstOrDefaultAsync(x => x.Id == input.UserId && x.TenantId == input.TenantId);
        if (user == null)
        {
            return new PasswordResetValidateResultDto
            {
                Status = PasswordResetValidationStatus.Invalid,
                Message = "Invalid password reset token."
            };
        }

        var token = DecodeResetToken(input.Token);
        var provider = _userManager.Options.Tokens.PasswordResetTokenProvider;
        var valid = await _userManager.VerifyUserTokenAsync(user, provider, "ResetPassword", token);
        return new PasswordResetValidateResultDto
        {
            Status = valid ? PasswordResetValidationStatus.Valid : PasswordResetValidationStatus.Invalid,
            Message = valid ? null : "Password reset token is invalid or expired."
        };
    }

    [DisableAuditing]
    public async Task<PasswordResetCompleteResultDto> CompletePasswordResetAsync(PasswordResetCompleteInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        NormalizePasswordResetCompleteInput(input);

        var result = new PasswordResetCompleteResultDto();
        if (input.TenantId == Guid.Empty || input.UserId == Guid.Empty || string.IsNullOrWhiteSpace(input.Token))
        {
            result.Errors.Add("Invalid password reset request.");
            return result;
        }

        if (!string.Equals(input.NewPassword, input.ConfirmPassword, StringComparison.Ordinal))
        {
            result.Errors.Add("Password confirmation does not match.");
            return result;
        }

        var user = await _userManager.Users.FirstOrDefaultAsync(x => x.Id == input.UserId && x.TenantId == input.TenantId);
        if (user == null)
        {
            result.Errors.Add("Invalid password reset token.");
            return result;
        }

        var token = DecodeResetToken(input.Token);
        var provider = _userManager.Options.Tokens.PasswordResetTokenProvider;
        var isValid = await _userManager.VerifyUserTokenAsync(user, provider, "ResetPassword", token);
        result.TokenStatus = isValid ? PasswordResetValidationStatus.Valid : PasswordResetValidationStatus.Invalid;
        if (!isValid)
        {
            result.Errors.Add("Password reset token is invalid or expired.");
            return result;
        }

        var resetResult = await _userManager.ResetPasswordAsync(user, token, input.NewPassword);
        if (!resetResult.Succeeded)
        {
            result.Errors.AddRange(resetResult.Errors.Select(x => x.Description));
            return result;
        }

        user.ShouldChangePasswordOnNextLogin = false;
        var clearedActivationBlock = false;
        if (user.RequireEmailActivationBeforeLogin)
        {
            user.RequireEmailActivationBeforeLogin = false;
            user.EmailConfirmed = true;
            clearedActivationBlock = true;
        }
        await _userManager.UpdateAsync(user);
        if (clearedActivationBlock)
        {
            await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
        }
        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        await _sessionRevocationService.RevokeAllAsync(user.Id, user.TenantId, action: "PasswordReset");

        await SaveSecurityLogAsync(
            identity: "Account",
            action: "PasswordResetCompleted",
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
        result.Succeeded = true;
        return result;
    }

    [DisableAuditing]
    public async Task<PasswordExpiredChangeResultDto> ChangeExpiredPasswordAsync(PasswordExpiredChangeInput input)
    {
        NormalizePasswordExpiredChangeInput(input);

        var result = new PasswordExpiredChangeResultDto();
        if (!IsValidInteractiveContext(input.Mode, input.TenantId))
        {
            result.Errors.Add("Invalid password change request.");
            return result;
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();

        var challenge = await ResolveInteractiveChallengeAsync(input, InteractiveLoginStep.PasswordExpired, CancellationToken.None);
        if (challenge == null)
        {
            result.Errors.Add("Invalid password change request.");
            return result;
        }

        var loginInput = challenge.LoginInput;
        var currentPassword = input.CurrentPassword;

        if (string.IsNullOrWhiteSpace(loginInput) || string.IsNullOrWhiteSpace(currentPassword))
        {
            result.Errors.Add("Invalid password change request.");
            return result;
        }

        if (!string.Equals(input.NewPassword, input.ConfirmPassword, StringComparison.Ordinal))
        {
            result.Errors.Add("Password confirmation does not match.");
            return result;
        }

        var user = await FindInteractiveUserAsync(challenge.Mode, challenge.TenantId, loginInput);
        if (user == null)
        {
            result.Errors.Add("Invalid user credentials.");
            return result;
        }

        var userTenantId = NormalizeUserTenantId(user);
        if (!user.ShouldChangePasswordOnNextLogin)
        {
            result.Errors.Add("Password expiration policy is not active for this account.");
            return result;
        }

        var changeResult = await _userManager.ChangePasswordAsync(user, currentPassword, input.NewPassword);
        if (!changeResult.Succeeded)
        {
            result.Errors.AddRange(changeResult.Errors.Select(x => x.Description));
            return result;
        }

        user.ShouldChangePasswordOnNextLogin = false;
        await _userManager.UpdateAsync(user);
        await _cacheInvalidator.InvalidateAuthorizationAsync(userTenantId, user.Id);
        await _sessionRevocationService.RevokeAllAsync(user.Id, userTenantId, action: "PasswordExpiredPasswordChanged");

        await SaveSecurityLogAsync(
            identity: "Account",
            action: "PasswordExpiredChanged",
            sessionId: null,
            userName: user.UserName,
            userId: user.Id,
            tenantId: userTenantId,
            clientId: null,
            clientIpAddress: null,
            browserInfo: null,
            correlationId: null,
            location: null);

        await unitOfWorkDbContext.SaveChangesAsync();
        result.Succeeded = true;
        return result;
    }
}
