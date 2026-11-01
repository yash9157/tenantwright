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
    public async Task<LoginResultDto> CheckPasswordAsync(
        string loginInput,
        string password,
        Guid? tenantId = null,
        InteractiveLoginMode mode = InteractiveLoginMode.Tenant,
        string? sessionId = null,
        string? clientId = null,
        string? clientIpAddress = null,
        string? browserInfo = null,
        string? correlationId = null,
        string? device = null,
        string? location = null,
        string? twoFactorCode = null,
        string? twoFactorRecoveryCode = null,
        string? deviceFingerprint = null,
        bool rememberTrustedDevice = false)
    {
        if (!IsValidInteractiveContext(mode, tenantId))
        {
            return new LoginResultDto { Succeeded = false };
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        string? markerSessionId = null;
        Guid? markerUserId = null;
        Guid? markerTenantId = null;
        LoginRiskEvaluationResult? riskEvaluation = null;
        Dictionary<string, object?>? riskExtraProperties = null;
        var riskOptions = _loginRiskOptions.Value;
        loginInput = NormalizeRequiredInput(loginInput, 256);
        sessionId = NormalizeOptionalInput(sessionId, 64);
        clientId = NormalizeOptionalInput(clientId, 128);
        clientIpAddress = NormalizeOptionalInput(clientIpAddress, 64);
        browserInfo = NormalizeOptionalInput(browserInfo, 1024);
        correlationId = NormalizeOptionalInput(correlationId, 128);
        device = NormalizeOptionalInput(device, 128);
        location = NormalizeOptionalInput(location, 128);
        deviceFingerprint = NormalizeOptionalInput(deviceFingerprint, 1024);
        twoFactorCode = NormalizeOptionalInput(twoFactorCode, 32);
        twoFactorRecoveryCode = NormalizeOptionalInput(twoFactorRecoveryCode, 128);

        // Resolve the user within the tenant when a tenant is supplied so password
        // verification and lockout never run against a user owned by another tenant.
        // A cross-tenant match is treated as "user not found" (generic failure, no
        // lockout), closing the cross-tenant lockout/credential-oracle vector.
        var user = await FindInteractiveUserAsync(mode, tenantId, loginInput);
        if (user == null)
        {
            await SaveSecurityLogAsync(
                identity: "Account",
                action: "LoginFailed",
                sessionId: sessionId,
                userName: loginInput,
                userId: null,
                tenantId: null,
                clientId: clientId,
                clientIpAddress: clientIpAddress,
                browserInfo: browserInfo,
                correlationId: correlationId,
                location: location);
            await unitOfWorkDbContext.SaveChangesAsync();
            return new LoginResultDto { Succeeded = false };
        }

        var userTenantId = NormalizeUserTenantId(user);
        var hasPasskey = await unitOfWorkDbContext.IdentityPasskeyCredentials
            .AsNoTracking()
            .AnyAsync(x => x.UserId == user.Id && x.TenantId == userTenantId);
        var mfaEnrolled = user.TwoFactorEnabled || hasPasskey;
        var result = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        var loginResult = new LoginResultDto
        {
            Succeeded = result.Succeeded,
            IsLockedOut = result.IsLockedOut,
            RequiresTwoFactor = result.RequiresTwoFactor,
            IsNotAllowed = result.IsNotAllowed,
            ShouldChangePasswordOnNextLogin = user.ShouldChangePasswordOnNextLogin
        };

        if ((result.Succeeded || result.RequiresTwoFactor) && RequiresEmailActivationBeforeLogin(user))
        {
            loginResult.Succeeded = false;
            loginResult.RequiresTwoFactor = false;
            loginResult.IsNotAllowed = true;
            loginResult.FailureMessage = "Verify your email address before signing in.";

            await SaveSecurityLogAsync(
                identity: "Account",
                action: "LoginActivationRequired",
                sessionId: sessionId,
                userName: user.UserName,
                userId: user.Id,
                tenantId: userTenantId,
                clientId: clientId,
                clientIpAddress: clientIpAddress,
                browserInfo: browserInfo,
                correlationId: correlationId,
                location: location);

            await unitOfWorkDbContext.SaveChangesAsync();
            return loginResult;
        }

        if (result.Succeeded || result.RequiresTwoFactor)
        {
            riskEvaluation = await _loginRiskEvaluator.EvaluateAsync(
                new LoginRiskEvaluationContext
                {
                    UserId = user.Id,
                    TenantId = userTenantId,
                    ClientIpAddress = clientIpAddress,
                    DeviceFingerprint = deviceFingerprint
                });

            loginResult.RiskLevel = riskEvaluation.Level;
            loginResult.RiskScore = riskEvaluation.Score;
            loginResult.RiskAction = riskEvaluation.Action;
            loginResult.RiskReasons = [.. riskEvaluation.Reasons];
            LoginRiskMetrics.RecordDecision(riskEvaluation.Level, riskEvaluation.Action, riskEvaluation.Score);

            riskExtraProperties = BuildRiskExtraProperties(
                riskEvaluation,
                deviceFingerprint,
                riskOptions.Mode.ToString(),
                blockedByRisk: false,
                mfaEnrolled: mfaEnrolled);
        }

        var hasTrustedDevice = await HasTrustedDeviceAsync(user.Id, userTenantId, deviceFingerprint);
        if (mfaEnrolled
            && result.Succeeded
            && string.IsNullOrWhiteSpace(twoFactorCode)
            && string.IsNullOrWhiteSpace(twoFactorRecoveryCode)
            && !hasTrustedDevice)
        {
            loginResult.Succeeded = false;
            loginResult.RequiresTwoFactor = true;
        }

        var enforceRiskDecisions = riskOptions.Enabled && riskOptions.Mode == LoginRiskMode.Enforce;
        if (enforceRiskDecisions && riskEvaluation != null && riskEvaluation.Action == LoginRiskAction.Block)
        {
            loginResult.Succeeded = false;
            loginResult.RequiresTwoFactor = false;
            loginResult.BlockedByRisk = true;
            loginResult.IsNotAllowed = true;

            riskExtraProperties = BuildRiskExtraProperties(
                riskEvaluation,
                deviceFingerprint,
                riskOptions.Mode.ToString(),
                blockedByRisk: true,
                mfaEnrolled: mfaEnrolled);

            await SaveSecurityLogAsync(
                identity: "Account",
                action: "LoginRiskBlocked",
                sessionId: sessionId,
                userName: user.UserName,
                userId: user.Id,
                tenantId: userTenantId,
                clientId: clientId,
                clientIpAddress: clientIpAddress,
                browserInfo: browserInfo,
                correlationId: correlationId,
                location: location,
                extraProperties: riskExtraProperties);

            await PublishRiskBlockedAlertAsync(user, clientIpAddress, clientId, riskEvaluation);

            await unitOfWorkDbContext.SaveChangesAsync();
            return loginResult;
        }

        var requiresStepUpMfa = loginResult.RequiresTwoFactor ||
                                (enforceRiskDecisions && riskEvaluation?.Action == LoginRiskAction.ForceMfa);

        if (result.Succeeded && !requiresStepUpMfa)
        {
            if (loginResult.ShouldChangePasswordOnNextLogin)
            {
                loginResult.Succeeded = false;
                loginResult.IsNotAllowed = true;
            }

            if (user.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
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

            if (loginResult.Succeeded)
            {
                markerSessionId = await UpsertIdentitySessionAsync(user.Id, userTenantId, sessionId, clientId, clientIpAddress, browserInfo, device, location);
                markerUserId = user.Id;
                markerTenantId = userTenantId;
            }

            await SaveSecurityLogAsync(
                identity: "Account",
                action: loginResult.Succeeded
                    ? "LoginSuccess"
                    : loginResult.ShouldChangePasswordOnNextLogin
                        ? "PasswordExpiredChallenge"
                        : "LoginNotAllowed",
                sessionId: sessionId,
                userName: user.UserName,
                userId: user.Id,
                tenantId: userTenantId,
                clientId: clientId,
                clientIpAddress: clientIpAddress,
                browserInfo: browserInfo,
                correlationId: correlationId,
                location: location,
                extraProperties: riskExtraProperties);

            if (loginResult.Succeeded &&
                riskEvaluation?.Reasons.Contains("new_device", StringComparer.Ordinal) == true)
            {
                await _securityAlertPublisher.PublishNewDeviceSignInAlertAsync(
                    userTenantId,
                    user.Id,
                    user.UserName,
                    user.Email,
                    clientIpAddress,
                    clientId,
                    browserInfo,
                    location,
                    DateTime.UtcNow);
            }
        }
        else if (requiresStepUpMfa)
        {
            if (!mfaEnrolled)
            {
                loginResult.Succeeded = false;
                loginResult.RequiresTwoFactor = false;
                loginResult.BlockedByRisk = true;
                loginResult.IsNotAllowed = true;

                await SaveSecurityLogAsync(
                    identity: "Account",
                    action: "LoginRiskMfaRequiredButNotEnrolled",
                    sessionId: sessionId,
                    userName: user.UserName,
                    userId: user.Id,
                    tenantId: userTenantId,
                    clientId: clientId,
                    clientIpAddress: clientIpAddress,
                    browserInfo: browserInfo,
                    correlationId: correlationId,
                    location: location,
                    extraProperties: BuildRiskExtraProperties(
                        riskEvaluation,
                        deviceFingerprint,
                        riskOptions.Mode.ToString(),
                        blockedByRisk: true,
                        mfaEnrolled: mfaEnrolled));

                await unitOfWorkDbContext.SaveChangesAsync();
                return loginResult;
            }

            var normalizedTwoFactorCode = NormalizeTwoFactorCode(twoFactorCode);
            var normalizedRecoveryCode = NormalizeRecoveryCode(twoFactorRecoveryCode);
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
                    markerSessionId = await UpsertIdentitySessionAsync(user.Id, userTenantId, sessionId, clientId, clientIpAddress, browserInfo, device, location);
                    markerUserId = user.Id;
                    markerTenantId = userTenantId;

                    if (rememberTrustedDevice)
                    {
                        await UpsertTrustedDeviceAsync(user.Id, userTenantId, deviceFingerprint, riskOptions.TrustedDeviceDays);
                    }
                }

                await SaveSecurityLogAsync(
                    identity: "Account",
                    action: usedRecoveryCode ? "TwoFactorRecoverySuccess" : "TwoFactorSuccess",
                    sessionId: sessionId,
                    userName: user.UserName,
                    userId: user.Id,
                    tenantId: userTenantId,
                    clientId: clientId,
                    clientIpAddress: clientIpAddress,
                    browserInfo: browserInfo,
                    correlationId: correlationId,
                    location: location,
                    extraProperties: riskExtraProperties);

                if (loginResult.Succeeded &&
                    riskEvaluation?.Reasons.Contains("new_device", StringComparer.Ordinal) == true)
                {
                    await _securityAlertPublisher.PublishNewDeviceSignInAlertAsync(
                        userTenantId,
                        user.Id,
                        user.UserName,
                        user.Email,
                        clientIpAddress,
                        clientId,
                        browserInfo,
                        location,
                        DateTime.UtcNow);
                }
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
                loginResult.RequiresTwoFactor = !loginResult.IsLockedOut;

                await SaveSecurityLogAsync(
                    identity: "Account",
                    action: action,
                    sessionId: sessionId,
                    userName: user.UserName,
                    userId: user.Id,
                    tenantId: userTenantId,
                    clientId: clientId,
                    clientIpAddress: clientIpAddress,
                    browserInfo: browserInfo,
                    correlationId: correlationId,
                    location: location,
                    extraProperties: riskExtraProperties);
            }
        }
        else
        {
            if (result.IsLockedOut)
            {
                await _cacheInvalidator.InvalidateAuthorizationAsync(userTenantId, user.Id);
                await _sessionRevocationService.RevokeAllAsync(
                    user.Id,
                    userTenantId,
                    action: "UserLockedOut");
            }

            await SaveSecurityLogAsync(
                identity: "Account",
                action: result.IsLockedOut ? "LoginLockedOut" : loginResult.RequiresTwoFactor ? "TwoFactorChallenge" : "LoginInvalidPassword",
                sessionId: sessionId,
                userName: user.UserName,
                userId: user.Id,
                tenantId: userTenantId,
                clientId: clientId,
                clientIpAddress: clientIpAddress,
                browserInfo: browserInfo,
                correlationId: correlationId,
                location: location,
                extraProperties: riskExtraProperties);
        }

        await unitOfWorkDbContext.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(markerSessionId) && markerUserId.HasValue)
        {
            await _sessionValidityCache.RemoveAsync(IdentitySessionCache.ValidKey(markerUserId.Value, markerSessionId));
            var (payload, keyTtl) = Shared.Caching.Sessions.SessionMarkerPayload.CreateInitial(
                _sessionValidationOptions.Value.MarkerTtlHours,
                _sessionValidationOptions.Value.IdleTimeoutMinutes,
                DateTime.UtcNow);

            await _sessionMarkerStore.SetActiveAsync(
                markerTenantId,
                markerUserId.Value,
                markerSessionId,
                payload,
                keyTtl);
        }

        if (loginResult.IsLockedOut)
        {
            LoginRiskMetrics.RecordLockout();
        }

        return loginResult;
    }

}
