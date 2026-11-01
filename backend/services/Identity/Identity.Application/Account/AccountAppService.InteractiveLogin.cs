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

    [DisableAuditing]
    public async Task<InteractiveLoginResultDto> StartInteractiveLoginAsync(InteractiveLoginStartInput input)
    {
        NormalizeInteractiveStartInput(input);
        if (!IsValidInteractiveContext(input.Mode, input.TenantId))
        {
            return new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = BuildInteractiveMessage(InteractiveLoginStep.Failed)
            };
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var loginAttemptProtection = _loginAttemptProtectionOptions.Value;
        if (loginAttemptProtection.Enabled)
        {
            var allowed = await TryConsumeLoginAttemptQuotaAsync(
                input.Mode,
                input.TenantId,
                input.LoginInput,
                input.ClientIpAddress,
                input.DeviceFingerprint,
                loginAttemptProtection,
                CancellationToken.None);

            if (!allowed)
            {
                await SaveSecurityLogAsync(
                    identity: "Account",
                    action: "LoginThrottled",
                    sessionId: input.SessionId,
                    userName: input.LoginInput,
                    userId: null,
                    tenantId: input.TenantId,
                    clientId: input.ClientId,
                    clientIpAddress: input.ClientIpAddress,
                    browserInfo: input.BrowserInfo,
                    correlationId: input.CorrelationId,
                    location: input.Location);

                await unitOfWorkDbContext.SaveChangesAsync();

                return new InteractiveLoginResultDto
                {
                    Step = InteractiveLoginStep.Failed,
                    Message = "Too many login attempts. Please try again shortly."
                };
            }
        }

        var loginResult = await CheckPasswordAsync(
            input.LoginInput,
            input.Password,
            tenantId: input.TenantId,
            mode: input.Mode,
            sessionId: input.SessionId,
            clientId: input.ClientId,
            clientIpAddress: input.ClientIpAddress,
            browserInfo: input.BrowserInfo,
            correlationId: input.CorrelationId,
            device: input.Device,
            location: input.Location,
            twoFactorCode: null,
            twoFactorRecoveryCode: null,
            deviceFingerprint: input.DeviceFingerprint,
            rememberTrustedDevice: false);

        var step = ResolveInteractiveStep(loginResult);
        if (step == InteractiveLoginStep.Succeeded)
        {
            var isTrustedDevice = false;
            if (!string.IsNullOrWhiteSpace(input.DeviceFingerprint))
            {
                var user = await FindInteractiveUserAsync(input.Mode, input.TenantId, input.LoginInput);
                if (user != null)
                {
                    isTrustedDevice = await HasTrustedDeviceAsync(user.Id, NormalizeUserTenantId(user), input.DeviceFingerprint);
                }
            }

            if (InteractiveTrustDevicePromptPolicy.ShouldPromptAfterSuccessfulLogin(
                    input.PromptForTrustDevice,
                    input.DeviceFingerprint,
                    isTrustedDevice))
            {
                step = InteractiveLoginStep.RequiresTrustDevice;
            }
        }

        var result = new InteractiveLoginResultDto
        {
            Step = step,
            LoginResult = loginResult,
            Message = step == InteractiveLoginStep.Failed && !string.IsNullOrWhiteSpace(loginResult.FailureMessage)
                ? loginResult.FailureMessage
                : BuildInteractiveMessage(step)
        };

        if (step is InteractiveLoginStep.RequiresMfa or InteractiveLoginStep.RequiresMfaEnrollment)
        {
            var challengeUser = await FindInteractiveUserAsync(input.Mode, input.TenantId, input.LoginInput);
            if (challengeUser != null)
            {
                var challengeTenantId = NormalizeUserTenantId(challengeUser);
                result.PasskeyAvailable = await unitOfWorkDbContext.IdentityPasskeyCredentials
                    .AsNoTracking()
                    .AnyAsync(x => x.UserId == challengeUser.Id && x.TenantId == challengeTenantId);
                var codeBasedMfa = await GetCodeBasedMfaAvailabilityAsync(challengeUser);
                result.AuthenticatorCodeAvailable = codeBasedMfa.AuthenticatorCodeAvailable;
                result.RecoveryCodeAvailable = codeBasedMfa.RecoveryCodeAvailable;
            }
        }

        if (RequiresInteractiveChallenge(step))
        {
            var challenge = CreateChallengeFromStartInput(input, step);
            await SaveInteractiveChallengeAsync(challenge, CancellationToken.None);
            result.ChallengeId = challenge.ChallengeId;
            result.ChallengeExpiresAtUtc = challenge.ExpiresAtUtc;
        }

        return result;
    }

    [DisableAuditing]
    public async Task<InteractiveLoginResultDto> CompleteMfaAsync(CompleteMfaInput input)
    {
        NormalizeInteractiveChallengeInput(input);
        if (!IsValidInteractiveContext(input.Mode, input.TenantId))
        {
            return new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = "Interactive login challenge expired. Start sign-in again."
            };
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input.TwoFactorCode = NormalizeOptionalInput(input.TwoFactorCode, 32);
        input.TwoFactorRecoveryCode = NormalizeOptionalInput(input.TwoFactorRecoveryCode, 128);

        var challenge = await ResolveInteractiveChallengeAsync(input, InteractiveLoginStep.RequiresMfa, CancellationToken.None);
        if (challenge == null)
        {
            return new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = "Interactive login challenge expired. Start sign-in again."
            };
        }

        var user = await FindInteractiveUserAsync(challenge.Mode, challenge.TenantId, challenge.LoginInput);
        if (user == null)
        {
            return new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = "Interactive login challenge expired. Start sign-in again."
            };
        }

        var userTenantId = NormalizeUserTenantId(user);
        var normalizedTwoFactorCode = NormalizeTwoFactorCode(input.TwoFactorCode);
        var normalizedRecoveryCode = NormalizeRecoveryCode(input.TwoFactorRecoveryCode);
        var loginResult = new LoginResultDto
        {
            Succeeded = false,
            RequiresTwoFactor = true,
            ShouldChangePasswordOnNextLogin = user.ShouldChangePasswordOnNextLogin
        };

        string? markerSessionId = null;
        var usedRecoveryCode = false;
        var twoFactorSucceeded = false;

        if (!string.IsNullOrWhiteSpace(normalizedTwoFactorCode))
        {
            var tokenProvider = _userManager.Options.Tokens.AuthenticatorTokenProvider;
            twoFactorSucceeded = await _userManager.VerifyTwoFactorTokenAsync(
                user,
                tokenProvider,
                normalizedTwoFactorCode);
        }
        else if (!string.IsNullOrWhiteSpace(normalizedRecoveryCode))
        {
            var recoveryResult = await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, normalizedRecoveryCode);
            twoFactorSucceeded = recoveryResult.Succeeded;
            usedRecoveryCode = recoveryResult.Succeeded;
        }

        if (twoFactorSucceeded)
        {
            loginResult.IsLockedOut = await _userManager.IsLockedOutAsync(user);
            if (loginResult.IsLockedOut)
            {
                loginResult.Succeeded = false;
                loginResult.RequiresTwoFactor = false;
                await _cacheInvalidator.InvalidateAuthorizationAsync(userTenantId, user.Id);
                await _sessionRevocationService.RevokeAllAsync(
                    user.Id,
                    userTenantId,
                    action: "UserLockedOut");

                await SaveSecurityLogAsync(
                    identity: "Account",
                    action: "TwoFactorLockedOut",
                    sessionId: challenge.SessionId,
                    userName: user.UserName,
                    userId: user.Id,
                    tenantId: userTenantId,
                    clientId: challenge.ClientId,
                    clientIpAddress: challenge.ClientIpAddress,
                    browserInfo: challenge.BrowserInfo,
                    correlationId: challenge.CorrelationId,
                    location: challenge.Location);
            }
        }

        if (twoFactorSucceeded && !loginResult.IsLockedOut)
        {
            var resetFailedCountResult = await _userManager.ResetAccessFailedCountAsync(user);
            if (!resetFailedCountResult.Succeeded)
            {
                throw new SharedValidationException(string.Join(", ", resetFailedCountResult.Errors.Select(x => x.Description)));
            }

            loginResult.Succeeded = true;
            loginResult.RequiresTwoFactor = false;
            loginResult.IsLockedOut = false;
            loginResult.BlockedByRisk = false;

            if (user.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
            {
                loginResult.Succeeded = false;
                loginResult.IsNotAllowed = true;
            }

            if (loginResult.Succeeded)
            {
                markerSessionId = await UpsertIdentitySessionAsync(
                    user.Id,
                    userTenantId,
                    challenge.SessionId,
                    challenge.ClientId,
                    challenge.ClientIpAddress,
                    challenge.BrowserInfo,
                    challenge.Device,
                    challenge.Location);

                if (InteractiveMfaTrustedDevicePolicy.ShouldPersistTrustedDevice(
                        input.RememberTrustedDevice,
                        challenge.DeviceFingerprint))
                {
                    await UpsertTrustedDeviceAsync(user.Id, userTenantId, challenge.DeviceFingerprint, _loginRiskOptions.Value.TrustedDeviceDays);
                }
            }

            await SaveSecurityLogAsync(
                identity: "Account",
                action: usedRecoveryCode ? "TwoFactorRecoverySuccess" : "TwoFactorSuccess",
                sessionId: challenge.SessionId,
                userName: user.UserName,
                userId: user.Id,
                tenantId: userTenantId,
                clientId: challenge.ClientId,
                clientIpAddress: challenge.ClientIpAddress,
                browserInfo: challenge.BrowserInfo,
                correlationId: challenge.CorrelationId,
                location: challenge.Location);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(normalizedTwoFactorCode) || !string.IsNullOrWhiteSpace(normalizedRecoveryCode))
            {
                var accessFailedResult = await _userManager.AccessFailedAsync(user);
                if (!accessFailedResult.Succeeded)
                {
                    throw new SharedValidationException(string.Join(", ", accessFailedResult.Errors.Select(x => x.Description)));
                }
            }

            loginResult.Succeeded = false;
            loginResult.IsLockedOut = await _userManager.IsLockedOutAsync(user);
            loginResult.RequiresTwoFactor = !loginResult.IsLockedOut;

            if (loginResult.IsLockedOut)
            {
                await _cacheInvalidator.InvalidateAuthorizationAsync(userTenantId, user.Id);
                await _sessionRevocationService.RevokeAllAsync(
                    user.Id,
                    userTenantId,
                    action: "UserLockedOut");
            }

            var action = string.IsNullOrWhiteSpace(normalizedTwoFactorCode) && string.IsNullOrWhiteSpace(normalizedRecoveryCode)
                ? "TwoFactorChallenge"
                : loginResult.IsLockedOut
                    ? "TwoFactorLockedOut"
                    : "TwoFactorInvalidCode";

            await SaveSecurityLogAsync(
                identity: "Account",
                action: action,
                sessionId: challenge.SessionId,
                userName: user.UserName,
                userId: user.Id,
                tenantId: userTenantId,
                clientId: challenge.ClientId,
                clientIpAddress: challenge.ClientIpAddress,
                browserInfo: challenge.BrowserInfo,
                correlationId: challenge.CorrelationId,
                location: challenge.Location);
        }

        await unitOfWorkDbContext.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(markerSessionId))
        {
            await _sessionValidityCache.RemoveAsync(IdentitySessionCache.ValidKey(user.Id, markerSessionId));
            var (payload, keyTtl) = Shared.Caching.Sessions.SessionMarkerPayload.CreateInitial(
                _sessionValidationOptions.Value.MarkerTtlHours,
                _sessionValidationOptions.Value.IdleTimeoutMinutes,
                DateTime.UtcNow);

            await _sessionMarkerStore.SetActiveAsync(
                userTenantId,
                user.Id,
                markerSessionId,
                payload,
                keyTtl);
        }

        if (loginResult.IsLockedOut)
        {
            LoginRiskMetrics.RecordLockout();
        }

        var step = ResolveInteractiveStep(loginResult);

        var result = new InteractiveLoginResultDto
        {
            Step = step,
            LoginResult = loginResult,
            Message = step == InteractiveLoginStep.Failed && !string.IsNullOrWhiteSpace(loginResult.FailureMessage)
                ? loginResult.FailureMessage
                : BuildInteractiveMessage(step)
        };

        if (RequiresInteractiveChallenge(step))
        {
            var nextChallenge = CreateChallengeFromExisting(challenge, step);
            await SaveInteractiveChallengeAsync(nextChallenge, CancellationToken.None);
            result.ChallengeId = nextChallenge.ChallengeId;
            result.ChallengeExpiresAtUtc = nextChallenge.ExpiresAtUtc;
        }

        return result;
    }

    [DisableAuditing]
    public async Task<InteractiveLoginResultDto> CompleteTrustDeviceAsync(CompleteTrustDeviceInput input)
    {
        NormalizeInteractiveChallengeInput(input);
        if (!IsValidInteractiveContext(input.Mode, input.TenantId))
        {
            return new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = "Interactive login challenge expired. Start sign-in again."
            };
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();

        var challenge = await ResolveInteractiveChallengeAsync(input, InteractiveLoginStep.RequiresTrustDevice, CancellationToken.None);
        if (challenge == null)
        {
            return new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = "Interactive login challenge expired. Start sign-in again."
            };
        }

        var user = await FindInteractiveUserAsync(challenge.Mode, challenge.TenantId, challenge.LoginInput);
        if (user == null)
        {
            return new InteractiveLoginResultDto
            {
                Step = InteractiveLoginStep.Failed,
                Message = "Interactive login challenge expired. Start sign-in again."
            };
        }

        var userTenantId = NormalizeUserTenantId(user);
        var loginResult = new LoginResultDto
        {
            Succeeded = false,
            ShouldChangePasswordOnNextLogin = user.ShouldChangePasswordOnNextLogin
        };

        string? markerSessionId = null;
        loginResult.IsLockedOut = await _userManager.IsLockedOutAsync(user);
        if (loginResult.IsLockedOut)
        {
            await _cacheInvalidator.InvalidateAuthorizationAsync(userTenantId, user.Id);
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                userTenantId,
                action: "UserLockedOut");
        }
        else if (user.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
        {
            loginResult.Succeeded = false;
            loginResult.IsNotAllowed = true;
        }
        else if (RequiresEmailActivationBeforeLogin(user))
        {
            loginResult.Succeeded = false;
            loginResult.IsNotAllowed = true;
            loginResult.FailureMessage = "Verify your email address before signing in.";
        }
        else
        {
            loginResult.Succeeded = true;
            markerSessionId = await UpsertIdentitySessionAsync(
                user.Id,
                userTenantId,
                challenge.SessionId,
                challenge.ClientId,
                challenge.ClientIpAddress,
                challenge.BrowserInfo,
                challenge.Device,
                challenge.Location);

            if (input.TrustDevice)
            {
                await UpsertTrustedDeviceAsync(user.Id, userTenantId, challenge.DeviceFingerprint, _loginRiskOptions.Value.TrustedDeviceDays);
            }
        }

        var action = loginResult.IsLockedOut
            ? "LoginLockedOut"
            : loginResult.Succeeded
                ? input.TrustDevice ? "TrustedDeviceAccepted" : "TrustedDeviceSkipped"
                : "LoginNotAllowed";

        await SaveSecurityLogAsync(
            identity: "Account",
            action: action,
            sessionId: challenge.SessionId,
            userName: user.UserName,
            userId: user.Id,
            tenantId: userTenantId,
            clientId: challenge.ClientId,
            clientIpAddress: challenge.ClientIpAddress,
            browserInfo: challenge.BrowserInfo,
            correlationId: challenge.CorrelationId,
            location: challenge.Location);

        await unitOfWorkDbContext.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(markerSessionId))
        {
            await _sessionValidityCache.RemoveAsync(IdentitySessionCache.ValidKey(user.Id, markerSessionId));
            var (payload, keyTtl) = Shared.Caching.Sessions.SessionMarkerPayload.CreateInitial(
                _sessionValidationOptions.Value.MarkerTtlHours,
                _sessionValidationOptions.Value.IdleTimeoutMinutes,
                DateTime.UtcNow);

            await _sessionMarkerStore.SetActiveAsync(
                userTenantId,
                user.Id,
                markerSessionId,
                payload,
                keyTtl);
        }

        if (loginResult.IsLockedOut)
        {
            LoginRiskMetrics.RecordLockout();
        }

        var step = ResolveInteractiveStep(loginResult);
        var result = new InteractiveLoginResultDto
        {
            Step = step,
            LoginResult = loginResult,
            Message = step == InteractiveLoginStep.Failed && !string.IsNullOrWhiteSpace(loginResult.FailureMessage)
                ? loginResult.FailureMessage
                : BuildInteractiveMessage(step)
        };

        if (RequiresInteractiveChallenge(step))
        {
            var nextChallenge = CreateChallengeFromExisting(challenge, step);
            await SaveInteractiveChallengeAsync(nextChallenge, CancellationToken.None);
            result.ChallengeId = nextChallenge.ChallengeId;
            result.ChallengeExpiresAtUtc = nextChallenge.ExpiresAtUtc;
        }

        return result;
    }
}
