using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Passkeys;
using Identity.Application.Passkeys;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.Account;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Caching.Sessions;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Account;

public partial class AccountAppService
{
    [DisableAuditing]
    public async Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyEnrollmentAsync(
        InteractivePasskeyOptionsInput input)
    {
        NormalizeInteractiveChallengeInput(input);
        if (!IsValidInteractiveContext(input.Mode, input.TenantId))
        {
            throw new SharedValidationException("Interactive login challenge expired. Start sign-in again.");
        }
        var (challenge, user) = await ResolvePasskeyChallengeContextAsync(
            input,
            InteractiveLoginStep.RequiresMfaEnrollment,
            consume: false);
        var service = RequirePasskeyCeremonyService();

        return await service.BeginRegistrationAsync(
            user,
            "Passkey enrolled during sign-in",
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            challenge.ChallengeId);
    }

    [DisableAuditing]
    public async Task<InteractiveLoginResultDto> CompleteInteractivePasskeyEnrollmentAsync(
        CompleteInteractivePasskeyInput input)
    {
        NormalizeInteractiveChallengeInput(input);
        if (!IsValidInteractiveContext(input.Mode, input.TenantId))
        {
            throw new SharedValidationException("Interactive login challenge expired. Start sign-in again.");
        }
        var (peekedChallenge, user) = await ResolvePasskeyChallengeContextAsync(
            input,
            InteractiveLoginStep.RequiresMfaEnrollment,
            consume: false);
        var service = RequirePasskeyCeremonyService();
        IdentityPasskeyCredential credential;
        try
        {
            credential = await service.CompleteRegistrationAsync(
                user,
                input.CeremonyId,
                input.ResponseJson,
                PasskeyCeremonyPurpose.InteractiveEnrollment,
                peekedChallenge.ChallengeId);
        }
        catch (PasskeyVerificationException exception)
        {
            return await RejectPasskeyAttemptAsync(
                user,
                peekedChallenge,
                "PasskeyEnrollmentRejected",
                exception.Outcome,
                PasskeyVerificationFailed());
        }
        catch (SharedValidationException)
        {
            return await RejectPasskeyAttemptAsync(
                user,
                peekedChallenge,
                "PasskeyEnrollmentRejected",
                "ceremony_rejected",
                PasskeyVerificationFailed());
        }

        var consumedChallenge = await ResolveInteractiveChallengeAsync(
            input,
            InteractiveLoginStep.RequiresMfaEnrollment,
            CancellationToken.None);
        if (consumedChallenge == null)
        {
            PasskeyMetrics.Record("interactive_enrollment", "challenge_rejected");
            return await RejectPasskeyAttemptAsync(
                user,
                peekedChallenge,
                "PasskeyEnrollmentRejected",
                "login_challenge_rejected",
                ExpiredPasskeyChallenge());
        }

        var result = await CompletePasskeyLoginAsync(
            user,
            consumedChallenge,
            "PasskeyEnrollmentSuccess",
            enrollmentCredential: credential);
        PasskeyMetrics.Record("interactive_enrollment", result.Step == InteractiveLoginStep.Succeeded ? "success" : "rejected");
        return result;
    }

    [DisableAuditing]
    public async Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyAssertionAsync(
        InteractivePasskeyOptionsInput input)
    {
        NormalizeInteractiveChallengeInput(input);
        if (!IsValidInteractiveContext(input.Mode, input.TenantId))
        {
            throw new SharedValidationException("Interactive login challenge expired. Start sign-in again.");
        }
        var (challenge, user) = await ResolvePasskeyChallengeContextAsync(
            input,
            InteractiveLoginStep.RequiresMfa,
            consume: false);
        var service = RequirePasskeyCeremonyService();
        return await service.BeginAssertionAsync(user, challenge.ChallengeId);
    }

    [DisableAuditing]
    public async Task<InteractiveLoginResultDto> CompleteInteractivePasskeyAssertionAsync(
        CompleteInteractivePasskeyInput input)
    {
        NormalizeInteractiveChallengeInput(input);
        if (!IsValidInteractiveContext(input.Mode, input.TenantId))
        {
            throw new SharedValidationException("Interactive login challenge expired. Start sign-in again.");
        }
        var (peekedChallenge, user) = await ResolvePasskeyChallengeContextAsync(
            input,
            InteractiveLoginStep.RequiresMfa,
            consume: false);
        var service = RequirePasskeyCeremonyService();
        VerifiedPasskeyAssertion assertion;
        try
        {
            assertion = await service.CompleteAssertionAsync(
                user,
                input.CeremonyId,
                input.ResponseJson,
                peekedChallenge.ChallengeId);
        }
        catch (PasskeyVerificationException exception)
        {
            return await RejectPasskeyAttemptAsync(
                user,
                peekedChallenge,
                "PasskeyAuthenticationRejected",
                exception.Outcome,
                PasskeyVerificationFailed());
        }
        catch (SharedValidationException)
        {
            return await RejectPasskeyAttemptAsync(
                user,
                peekedChallenge,
                "PasskeyAuthenticationRejected",
                "ceremony_rejected",
                PasskeyVerificationFailed());
        }

        var codeBasedMfa = await GetCodeBasedMfaAvailabilityAsync(user);
        if (!assertion.UserVerified && !codeBasedMfa.IsAvailable)
        {
            PasskeyMetrics.Record("interactive_assertion", "additional_factor_unavailable");
            return await RejectPasskeyAttemptAsync(
                user,
                peekedChallenge,
                "PasskeyAuthenticationAdditionalFactorUnavailable",
                "user_verification_missing_no_code_factor",
                PasskeyUserVerificationUnavailable(peekedChallenge));
        }

        var consumedChallenge = await ResolveInteractiveChallengeAsync(
            input,
            InteractiveLoginStep.RequiresMfa,
            CancellationToken.None);
        if (consumedChallenge == null)
        {
            PasskeyMetrics.Record("interactive_assertion", "challenge_rejected");
            return await RejectPasskeyAttemptAsync(
                user,
                peekedChallenge,
                "PasskeyAuthenticationRejected",
                "login_challenge_rejected",
                ExpiredPasskeyChallenge());
        }

        var requiresAdditionalFactor = !assertion.UserVerified;
        var result = await CompletePasskeyLoginAsync(
            user,
            consumedChallenge,
            requiresAdditionalFactor
                ? "PasskeyAuthenticationAdditionalFactorRequired"
                : "PasskeyAuthenticationSuccess",
            assertion: assertion,
            requireAdditionalFactor: requiresAdditionalFactor,
            codeBasedMfa: codeBasedMfa);
        PasskeyMetrics.Record(
            "interactive_assertion",
            result.Step == InteractiveLoginStep.Succeeded
                ? "success"
                : result.Step == InteractiveLoginStep.RequiresMfa && requiresAdditionalFactor
                    ? "additional_factor_required"
                    : "rejected");
        return result;
    }

    private async Task<(InteractiveLoginChallengeCacheItem Challenge, ApplicationUser User)> ResolvePasskeyChallengeContextAsync(
        InteractiveChallengeInput input,
        InteractiveLoginStep expectedStep,
        bool consume)
    {
        if (!input.ChallengeId.HasValue || input.ChallengeId.Value == Guid.Empty)
        {
            throw new SharedValidationException("Interactive login challenge expired. Start sign-in again.");
        }

        InteractiveLoginChallengeCacheItem? challenge;
        if (consume)
        {
            challenge = await ResolveInteractiveChallengeAsync(input, expectedStep, CancellationToken.None);
        }
        else
        {
            challenge = await _interactiveLoginChallengeCache.GetAsync(
                BuildInteractiveChallengeCacheKey(input.ChallengeId.Value));
            if (challenge == null ||
                challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                challenge.Mode != input.Mode ||
                challenge.TenantId != input.TenantId ||
                !InteractiveChallengeStepGuard.MatchesExpectedStep(challenge, expectedStep))
            {
                challenge = null;
            }
        }

        if (challenge == null)
        {
            throw new SharedValidationException("Interactive login challenge expired. Start sign-in again.");
        }

        var user = await FindInteractiveUserAsync(challenge.Mode, challenge.TenantId, challenge.LoginInput);
        if (user == null || NormalizeUserTenantId(user) != challenge.TenantId)
        {
            throw new SharedValidationException("Interactive login challenge expired. Start sign-in again.");
        }

        return (challenge, user);
    }

    private async Task<InteractiveLoginResultDto> CompletePasskeyLoginAsync(
        ApplicationUser user,
        InteractiveLoginChallengeCacheItem challenge,
        string successAction,
        IdentityPasskeyCredential? enrollmentCredential = null,
        VerifiedPasskeyAssertion? assertion = null,
        bool requireAdditionalFactor = false,
        CodeBasedMfaAvailability codeBasedMfa = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userTenantId = NormalizeUserTenantId(user);
        var loginResult = new LoginResultDto
        {
            Succeeded = false,
            ShouldChangePasswordOnNextLogin = user.ShouldChangePasswordOnNextLogin
        };

        loginResult.IsLockedOut = await _userManager.IsLockedOutAsync(user);
        if (loginResult.IsLockedOut)
        {
            await _cacheInvalidator.InvalidateAuthorizationAsync(userTenantId, user.Id);
            await _sessionRevocationService.RevokeAllAsync(user.Id, userTenantId, action: "UserLockedOut");
        }
        else if (user.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
        {
            loginResult.IsNotAllowed = true;
        }
        else if (RequiresEmailActivationBeforeLogin(user))
        {
            loginResult.IsNotAllowed = true;
            loginResult.FailureMessage = "Verify your email address before signing in.";
        }
        else if (user.ShouldChangePasswordOnNextLogin)
        {
            loginResult.IsNotAllowed = true;
        }
        else
        {
            var resetResult = await _userManager.ResetAccessFailedCountAsync(user);
            if (!resetResult.Succeeded)
            {
                throw new SharedValidationException(string.Join(", ", resetResult.Errors.Select(x => x.Description)));
            }

            loginResult.Succeeded = true;
        }

        string? markerSessionId = null;
        if (loginResult.Succeeded)
        {
            if (enrollmentCredential != null)
            {
                await unitOfWorkDbContext.IdentityPasskeyCredentials.AddAsync(enrollmentCredential);
            }

            assertion?.Apply();
            if (!requireAdditionalFactor)
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
            }
        }

        var action = loginResult.IsLockedOut
            ? "PasskeyAuthenticationLockedOut"
            : loginResult.ShouldChangePasswordOnNextLogin
                ? "PasswordExpiredChallenge"
                : loginResult.Succeeded
                    ? successAction
                    : "PasskeyAuthenticationNotAllowed";
        await SaveSecurityLogAsync(
            identity: "Passkey",
            action: action,
            sessionId: challenge.SessionId,
            userName: user.UserName,
            userId: user.Id,
            tenantId: userTenantId,
            clientId: challenge.ClientId,
            clientIpAddress: challenge.ClientIpAddress,
            browserInfo: challenge.BrowserInfo,
            correlationId: challenge.CorrelationId,
            location: challenge.Location,
            extraProperties: requireAdditionalFactor && loginResult.Succeeded
                ? new Dictionary<string, object?>
                {
                    ["Outcome"] = "user_verification_missing"
                }
                : null);
        await unitOfWorkDbContext.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(markerSessionId))
        {
            await _sessionValidityCache.RemoveAsync(IdentitySessionCache.ValidKey(user.Id, markerSessionId));
            var (payload, keyTtl) = SessionMarkerPayload.CreateInitial(
                _sessionValidationOptions.Value.MarkerTtlHours,
                _sessionValidationOptions.Value.IdleTimeoutMinutes,
                DateTime.UtcNow);
            await _sessionMarkerStore.SetActiveAsync(userTenantId, user.Id, markerSessionId, payload, keyTtl);
        }

        var step = requireAdditionalFactor && loginResult.Succeeded
            ? InteractiveLoginStep.RequiresMfa
            : ResolveInteractiveStep(loginResult);
        var result = new InteractiveLoginResultDto
        {
            Step = step,
            LoginResult = requireAdditionalFactor && loginResult.Succeeded
                ? new LoginResultDto
                {
                    Succeeded = false,
                    ShouldChangePasswordOnNextLogin = loginResult.ShouldChangePasswordOnNextLogin
                }
                : loginResult,
            Message = BuildInteractiveMessage(step),
            PasskeyAvailable = !requireAdditionalFactor,
            AuthenticatorCodeAvailable = requireAdditionalFactor && codeBasedMfa.AuthenticatorCodeAvailable,
            RecoveryCodeAvailable = requireAdditionalFactor && codeBasedMfa.RecoveryCodeAvailable
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

    private async Task<InteractiveLoginResultDto> RejectPasskeyAttemptAsync(
        ApplicationUser user,
        InteractiveLoginChallengeCacheItem challenge,
        string action,
        string outcome,
        InteractiveLoginResultDto failureResult)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        await SaveSecurityLogAsync(
                    identity: "Passkey",
                    action: action,
                    sessionId: challenge.SessionId,
                    userName: user.UserName,
                    userId: user.Id,
                    tenantId: NormalizeUserTenantId(user),
                    clientId: challenge.ClientId,
                    clientIpAddress: challenge.ClientIpAddress,
                    browserInfo: challenge.BrowserInfo,
                    correlationId: challenge.CorrelationId,
                    location: challenge.Location,
                    extraProperties: new Dictionary<string, object?>
                    {
                        ["Outcome"] = outcome
                    });
        await unitOfWorkDbContext.SaveChangesAsync();

        return failureResult;
    }

    private PasskeyCeremonyService RequirePasskeyCeremonyService()
        => _passkeyCeremonyService ?? throw new SharedException("Passkeys are not available.");

    private async Task<CodeBasedMfaAvailability> GetCodeBasedMfaAvailabilityAsync(ApplicationUser user)
    {
        if (!user.TwoFactorEnabled)
        {
            return default;
        }

        var authenticatorProvider = _userManager.Options.Tokens.AuthenticatorTokenProvider;
        var validProviders = await _userManager.GetValidTwoFactorProvidersAsync(user);
        var authenticatorCodeAvailable = validProviders.Contains(authenticatorProvider, StringComparer.Ordinal);
        var recoveryCodeAvailable = await _userManager.CountRecoveryCodesAsync(user) > 0;
        return new CodeBasedMfaAvailability(authenticatorCodeAvailable, recoveryCodeAvailable);
    }

    private static InteractiveLoginResultDto ExpiredPasskeyChallenge()
    {
        return new InteractiveLoginResultDto
        {
            Step = InteractiveLoginStep.Failed,
            Message = "Interactive login challenge expired. Start sign-in again."
        };
    }

    internal static InteractiveLoginResultDto PasskeyVerificationFailed()
    {
        return new InteractiveLoginResultDto
        {
            Step = InteractiveLoginStep.Failed,
            Message = "Passkey verification failed. Start sign-in again."
        };
    }

    private static InteractiveLoginResultDto PasskeyUserVerificationUnavailable(
        InteractiveLoginChallengeCacheItem challenge)
    {
        return new InteractiveLoginResultDto
        {
            Step = InteractiveLoginStep.Failed,
            Message = "This passkey did not confirm device verification. Try a passkey from your phone, Windows Hello, or a security key.",
            ChallengeId = challenge.ChallengeId,
            ChallengeExpiresAtUtc = challenge.ExpiresAtUtc,
            PasskeyAvailable = true,
            AuthenticatorCodeAvailable = false,
            RecoveryCodeAvailable = false
        };
    }

    private readonly record struct CodeBasedMfaAvailability(
        bool AuthenticatorCodeAvailable,
        bool RecoveryCodeAvailable)
    {
        public bool IsAvailable => AuthenticatorCodeAvailable || RecoveryCodeAvailable;
    }
}
