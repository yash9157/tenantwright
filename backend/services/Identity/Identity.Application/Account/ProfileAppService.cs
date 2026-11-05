using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System;
using System.Threading.Tasks;
using Identity.Application.Contracts.Caching;
using Identity.Application.Caching;
using Identity.Application.Contracts.Account;
using Identity.Application.ApplicationUsers;
using Identity.Application.SecurityLogs;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.Security;
using System.Text;
using System.Text.Json;
using Identity.Application.Contracts.SecurityLogs;
using Shared.EventContracts;
using Shared.EventBus.Messaging;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Account;

[Authorize]
public class ProfileAppService : IProfileAppService, ITransientDependency
{
    private const string MfaIssuer = "TenantWright";
    private const string ConfirmEmailPublicPath = "/auth/email-verification/confirm";

    private readonly ApplicationUserManager _userManager;
    private readonly ICurrentUser _currentUser;
    private readonly IIdentityAuthCacheInvalidator _cacheInvalidator;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly Identity.Application.Contracts.Users.IIdentitySessionRevocationService _sessionRevocationService;
    private readonly IdentityUserSecurityAlertPublisher _securityAlertPublisher;
    private readonly IOptions<EmailVerificationOptions> _emailVerificationOptions;
    private readonly ITenantWrightMessageSender _eventBus;

    public ProfileAppService(
        ApplicationUserManager userManager, ICurrentUser currentUser, IIdentityAuthCacheInvalidator cacheInvalidator, IDbContextProvider<IdentityDbContextApp> dbContextProvider, IHttpContextAccessor httpContextAccessor, Identity.Application.Contracts.Users.IIdentitySessionRevocationService sessionRevocationService, IdentityUserSecurityAlertPublisher securityAlertPublisher, IOptions<EmailVerificationOptions> emailVerificationOptions, ITenantWrightMessageSender eventBus)
    {
        _userManager = userManager;
        _currentUser = currentUser;
        _cacheInvalidator = cacheInvalidator;
        _dbContextProvider = dbContextProvider;
        _httpContextAccessor = httpContextAccessor;
        _sessionRevocationService = sessionRevocationService;
        _securityAlertPublisher = securityAlertPublisher;
        _emailVerificationOptions = emailVerificationOptions;
        _eventBus = eventBus;
    }

    public virtual async Task<ProfileDto> GetAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);

        var tenantId = _currentUser.TenantId;
        var activeSessions = unitOfWorkDbContext.IdentitySessions
            .AsNoTracking()
            .Where(session => session.UserId == user.Id && session.Status == IdentitySessionStatus.Active);
        activeSessions = tenantId.HasValue
            ? activeSessions.Where(session => session.TenantId == tenantId.Value)
            : activeSessions.Where(session => session.TenantId == null);

        var activeSessionCount = await activeSessions.CountAsync();
        var lastSignInAtUtc = await activeSessions.MaxAsync(session => (DateTime?)session.CreationTime);

        return new ProfileDto
        {
            UserName = user.UserName!,
            Email = user.Email!,
            PendingEmail = user.PendingEmail,
            Name = user.Name,
            Surname = user.Surname,
            PhoneNumber = user.PhoneNumber,
            HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
            TwoFactorEnabled = user.TwoFactorEnabled,
            ConcurrencyStamp = user.ConcurrencyStamp,
            ActiveSessionCount = activeSessionCount,
            LastSignInAtUtc = lastSignInAtUtc
        };
    }


    [Audited]
    public virtual async Task<ProfileDto> UpdateAsync(UpdateProfileDto input)
    {
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        var originalAliasHashes = IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber);

        if (!string.Equals(user.UserName, input.UserName, StringComparison.OrdinalIgnoreCase))
        {
            var result = await _userManager.SetUserNameAsync(user, input.UserName);
            if (!result.Succeeded) throw new SharedValidationException(string.Join(", ", result.Errors.Select(x => x.Description)));
        }

        var emailChanged = !string.Equals(user.Email, input.Email, StringComparison.OrdinalIgnoreCase);
        if (emailChanged)
        {
            var pendingEmail = NormalizeEmail(input.Email);
            var safeBaseUrl = NormalizeAndValidatePublicBaseUrl(
                input.EmailConfirmationBaseUrl,
                _emailVerificationOptions.Value.AllowedBaseUrls);
            if (string.IsNullOrWhiteSpace(safeBaseUrl))
            {
                throw new SharedValidationException("A valid email confirmation base URL is required.");
            }

            await EnsureEmailChangeProofAsync(user, input);
            user.PendingEmail = pendingEmail;
            user.PendingEmailRequestedAtUtc = DateTime.UtcNow;
            await QueuePendingEmailConfirmationAsync(user, pendingEmail, safeBaseUrl);
        }

        user.Name = IdentityProfileName.Normalize(input.Name, "Name");
        user.Surname = IdentityProfileName.Normalize(input.Surname, "Surname");
        user.PhoneNumber = input.PhoneNumber;
        user.ConcurrencyStamp = input.ConcurrencyStamp;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded) throw new SharedValidationException(string.Join(", ", updateResult.Errors.Select(x => x.Description)));

        var mergedAliasHashes = originalAliasHashes
            .Concat(IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
        await _cacheInvalidator.InvalidateAliasesAsync(user.TenantId, mergedAliasHashes);

        return await GetAsync();
    }

    [Audited]
    public virtual async Task<ProfileDto> ResendPendingEmailConfirmationAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        if (string.IsNullOrWhiteSpace(user.PendingEmail))
        {
            throw new SharedValidationException("There is no pending email change to resend.");
        }

        var safeBaseUrl = GetServerOwnedEmailConfirmationBaseUrl();
        await QueuePendingEmailConfirmationAsync(user, user.PendingEmail, safeBaseUrl);
        await unitOfWorkDbContext.SaveChangesAsync();
        return await GetAsync();
    }

    [Audited]
    public virtual async Task<ProfileDto> CancelPendingEmailChangeAsync()
    {
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        if (string.IsNullOrWhiteSpace(user.PendingEmail))
        {
            return await GetAsync();
        }

        user.PendingEmail = null;
        user.PendingEmailRequestedAtUtc = null;
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", updateResult.Errors.Select(x => x.Description)));
        }

        await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
        return await GetAsync();
    }

    private async Task EnsureEmailChangeProofAsync(ApplicationUser user, UpdateProfileDto input)
    {
        if (!string.IsNullOrWhiteSpace(input.CurrentPassword) &&
            await _userManager.CheckPasswordAsync(user, input.CurrentPassword))
        {
            return;
        }

        var twoFactorCode = NormalizeTwoFactorCode(input.TwoFactorCode);
        if (!string.IsNullOrWhiteSpace(twoFactorCode))
        {
            var tokenProvider = _userManager.Options.Tokens.AuthenticatorTokenProvider;
            if (await _userManager.VerifyTwoFactorTokenAsync(user, tokenProvider, twoFactorCode))
            {
                return;
            }
        }

        var recoveryCode = NormalizeRecoveryCode(input.TwoFactorRecoveryCode);
        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            var recoveryResult = await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode);
            if (recoveryResult.Succeeded)
            {
                return;
            }
        }

        throw new SharedValidationException("Changing email requires current password or MFA re-authentication.");
    }

    private async Task EnsureMfaSensitiveOperationProofAsync(
        ApplicationUser user,
        MfaSensitiveOperationProofInput? input)
    {
        if (string.IsNullOrWhiteSpace(input?.CurrentPassword) ||
            !await _userManager.CheckPasswordAsync(user, input.CurrentPassword))
        {
            throw new SharedValidationException("Current password verification is required for this MFA security action.");
        }
    }

    private async Task QueuePendingEmailConfirmationAsync(ApplicationUser user, string pendingEmail, string safeBaseUrl)
    {
        var options = _emailVerificationOptions.Value;
        if (!options.Enabled)
        {
            throw new SharedValidationException("Email verification is disabled.");
        }

        var token = await _userManager.GenerateChangeEmailTokenAsync(user, pendingEmail);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link =
            $"{safeBaseUrl}{ConfirmEmailPublicPath}?tenantId={user.TenantId:D}&userId={user.Id:D}&token={Uri.EscapeDataString(encodedToken)}";

        var command = new NotificationSendRequestedHostEto
        {
            RequestId = Guid.CreateVersion7(),
            OccurredAtUtc = DateTime.UtcNow,
            SourceService = "identity-service",
            DeliveryPlan = new NotificationDeliveryPlan
            {
                Mode = NotificationDeliveryMode.Parallel,
                Steps = [new NotificationChannelPlan
                    {
                        Channel = NotificationSendChannel.Email,
                        Subject = "Confirm your new email address",
                        TemplateName = options.TemplateName,
                        TemplateModel = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["link"] = link,
                ["userId"] = user.Id.ToString("D"),
                ["userName"] = user.UserName,
                ["newEmail"] = pendingEmail
            }
                    }]
            },
            Recipients = IdentityNotificationContractPublisher.CreateSingleChannelRecipients(NotificationSendChannel.Email, [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = pendingEmail
                }
            ], null),
        };

        Guid? tenantId = user.TenantId == Guid.Empty ? null : user.TenantId;
        await IdentityNotificationContractPublisher.PublishAsync(_eventBus, tenantId, command);
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new SharedValidationException("Email is required.");
        }

        return email.Trim();
    }

    private static string? NormalizeTwoFactorCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string? NormalizeRecoveryCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code) ? null : code.Trim();
    }

    private static string? NormalizeAndValidatePublicBaseUrl(string? baseUrl, IReadOnlyCollection<string> allowedBaseUrls)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
        return allowedBaseUrls.Any(x => string.Equals(x.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : null;
    }

    private string GetServerOwnedEmailConfirmationBaseUrl()
    {
        var baseUrl = _emailVerificationOptions.Value.AllowedBaseUrls.FirstOrDefault();
        var safeBaseUrl = NormalizeAndValidatePublicBaseUrl(baseUrl, _emailVerificationOptions.Value.AllowedBaseUrls);
        if (string.IsNullOrWhiteSpace(safeBaseUrl))
        {
            throw new SharedException("Email confirmation delivery is not configured.");
        }

        return safeBaseUrl;
    }

    public virtual async Task<UserNotificationSecurityPreferencesDto> GetPreferencesAsync()
    {
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        var preferences = await GetOrCreatePreferencesAsync(user);
        return MapPreferences(preferences);
    }

    [Audited]
    public virtual async Task<UserNotificationSecurityPreferencesDto> UpdatePreferencesAsync(UpdateUserNotificationSecurityPreferencesInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input ??= new UpdateUserNotificationSecurityPreferencesInput();

        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        var preferences = await GetOrCreatePreferencesAsync(user);
        preferences.EmailEnabled = input.Channels.EmailEnabled;
        preferences.NewDeviceSignInAlertEnabled = input.SecurityAlerts.NewDeviceSignInAlertEnabled;
        preferences.RiskBlockedAlertEnabled = input.SecurityAlerts.RiskBlockedAlertEnabled;
        preferences.AdminSessionRevokedAlertEnabled = input.SecurityAlerts.AdminSessionRevokedAlertEnabled;
        preferences.MfaChangedAlertEnabled = input.SecurityAlerts.MfaChangedAlertEnabled;
        preferences.LastModificationTime = DateTime.UtcNow;
        await unitOfWorkDbContext.SaveChangesAsync();

        return MapPreferences(preferences);
    }


    [Audited]
    public virtual async Task ChangePasswordAsync(ChangePasswordInput input)
    {
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            var result = await _userManager.AddPasswordAsync(user, input.NewPassword);
            if (!result.Succeeded) throw new SharedValidationException(string.Join(", ", result.Errors.Select(x => x.Description)));
            await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                user.TenantId,
                action: "PasswordChanged");
            await SaveSecurityLogAsync(user, "PasswordChanged");
            return;
        }

        var changeResult = await _userManager.ChangePasswordAsync(user, input.CurrentPassword!, input.NewPassword);
        if (!changeResult.Succeeded) throw new SharedValidationException(string.Join(", ", changeResult.Errors.Select(x => x.Description)));
        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        await _sessionRevocationService.RevokeAllAsync(
            user.Id,
            user.TenantId,
            action: "PasswordChanged");
        await SaveSecurityLogAsync(user, "PasswordChanged");
    }

    public virtual async Task<MfaStatusDto> GetMfaStatusAsync()
    {
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        return await BuildMfaStatusAsync(user);
    }

    [Audited]
    public virtual async Task<MfaSetupDto> GenerateMfaSetupAsync(MfaSensitiveOperationProofInput input)
    {
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        await EnsureMfaSensitiveOperationProofAsync(user, input);
        var resetResult = await _userManager.ResetAuthenticatorKeyAsync(user);
        if (!resetResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", resetResult.Errors.Select(x => x.Description)));
        }

        var authenticatorKey = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(authenticatorKey))
        {
            throw new SharedException("Authenticator key generation failed.");
        }

        await SaveSecurityLogAsync(user, "TwoFactorSetupGenerated");

        var status = await BuildMfaStatusAsync(user);
        return new MfaSetupDto
        {
            IsEnabled = status.IsEnabled,
            RecoveryCodesLeft = status.RecoveryCodesLeft,
            SharedKey = FormatKey(authenticatorKey),
            AuthenticatorUri = GenerateQrCodeUri(user, authenticatorKey)
        };
    }

    [Audited]
    public virtual async Task<MfaSetupDto> EnableMfaAsync(EnableMfaInput input)
    {
        if (string.IsNullOrWhiteSpace(input?.Code))
        {
            throw new SharedValidationException("Authenticator code is required.");
        }

        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        var authenticatorKey = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(authenticatorKey))
        {
            throw new SharedValidationException("Generate MFA setup first.");
        }

        var tokenProvider = _userManager.Options.Tokens.AuthenticatorTokenProvider;
        var isValid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            tokenProvider,
            NormalizeCode(input.Code));

        if (!isValid)
        {
            throw new SharedValidationException("Invalid authenticator code.");
        }

        var enableResult = await _userManager.SetTwoFactorEnabledAsync(user, true);
        if (!enableResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", enableResult.Errors.Select(x => x.Description)));
        }

        var recoveryCodes = (await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToList() ?? [];
        await SaveSecurityLogAsync(user, "TwoFactorEnabled");
        await _securityAlertPublisher.PublishMfaChangedAlertAsync(
            user.TenantId,
            user.Id,
            user.UserName,
            user.Email,
            changeType: "enabled",
            occurredAtUtc: DateTime.UtcNow);

        var status = await BuildMfaStatusAsync(user);
        return new MfaSetupDto
        {
            IsEnabled = status.IsEnabled,
            RecoveryCodesLeft = status.RecoveryCodesLeft,
            SharedKey = FormatKey(authenticatorKey),
            AuthenticatorUri = GenerateQrCodeUri(user, authenticatorKey),
            RecoveryCodes = recoveryCodes
        };
    }

    [Audited]
    public virtual async Task DisableMfaAsync(MfaSensitiveOperationProofInput input)
    {
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        await EnsureMfaSensitiveOperationProofAsync(user, input);
        if (!user.TwoFactorEnabled)
        {
            return;
        }

        var disableResult = await _userManager.SetTwoFactorEnabledAsync(user, false);
        if (!disableResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", disableResult.Errors.Select(x => x.Description)));
        }

        await SaveSecurityLogAsync(user, "TwoFactorDisabled");
        await _securityAlertPublisher.PublishMfaChangedAlertAsync(
            user.TenantId,
            user.Id,
            user.UserName,
            user.Email,
            changeType: "disabled",
            occurredAtUtc: DateTime.UtcNow);
    }

    [Audited]
    public virtual async Task<MfaSetupDto> RegenerateRecoveryCodesAsync(MfaSensitiveOperationProofInput input)
    {
        var user = await _userManager.GetByIdAsync(_currentUser.Id!.Value);
        await EnsureMfaSensitiveOperationProofAsync(user, input);
        if (!user.TwoFactorEnabled)
        {
            throw new SharedValidationException("Two-factor authentication is not enabled.");
        }

        var recoveryCodes = (await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToList() ?? [];
        var authenticatorKey = await _userManager.GetAuthenticatorKeyAsync(user) ?? string.Empty;
        await SaveSecurityLogAsync(user, "TwoFactorRecoveryCodesRegenerated");

        var status = await BuildMfaStatusAsync(user);
        return new MfaSetupDto
        {
            IsEnabled = status.IsEnabled,
            RecoveryCodesLeft = status.RecoveryCodesLeft,
            SharedKey = FormatKey(authenticatorKey),
            AuthenticatorUri = GenerateQrCodeUri(user, authenticatorKey),
            RecoveryCodes = recoveryCodes
        };
    }

    public virtual async Task<PagedResultDto<SecurityLogDto>> GetMySecurityLogsPagedAsync(SecurityLogListInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input ??= new SecurityLogListInput();
        var currentUserId = _currentUser.Id ?? throw new SharedException("Current user context is unavailable.");
        var query = ApplyCurrentUserScope(unitOfWorkDbContext.IdentitySecurityLogs.AsQueryable(), currentUserId);

        if (!string.IsNullOrWhiteSpace(input.Action))
        {
            query = query.Where(x => x.Action == input.Action);
        }

        if (!string.IsNullOrWhiteSpace(input.ClientId))
        {
            query = query.Where(x => x.ClientId == input.ClientId);
        }

        if (!string.IsNullOrWhiteSpace(input.UserName))
        {
            query = query.Where(x => x.UserName != null && x.UserName == input.UserName);
        }

        if (input.FromUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime >= input.FromUtc.Value);
        }

        if (input.ToUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime <= input.ToUtc.Value);
        }

        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);

        var totalCount = await query.LongCountAsync();
        var items = await query
            .OrderByDescending(x => x.CreationTime)
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();

        return new PagedResultDto<SecurityLogDto>
        {
            TotalCount = totalCount,
            Items = items.Select(MapSecurityLogToDto).ToList()
        };
    }

    public virtual async Task<PagedResultDto<SecurityLogDto>> GetMyRiskDecisionsPagedAsync(ProfileRiskDecisionListInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input ??= new ProfileRiskDecisionListInput();
        var currentUserId = _currentUser.Id ?? throw new SharedException("Current user context is unavailable.");
        var query = ApplyCurrentUserScope(unitOfWorkDbContext.IdentitySecurityLogs.AsQueryable(), currentUserId)
            .Where(x => x.Action == "LoginRiskBlocked");

        if (input.FromUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime >= input.FromUtc.Value);
        }

        if (input.ToUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime <= input.ToUtc.Value);
        }

        if (!input.IncludeFalsePositive)
        {
            query = query.Where(x =>
                x.FalsePositive != true &&
                (x.FalsePositive != null ||
                 x.ExtraProperties == null ||
                 !x.ExtraProperties.Contains("\"falsePositive\":true")));
        }

        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);

        var totalCount = await query.LongCountAsync();
        var items = await query
            .OrderByDescending(x => x.CreationTime)
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();

        return new PagedResultDto<SecurityLogDto>
        {
            TotalCount = totalCount,
            Items = items.Select(MapSecurityLogToDto).ToList()
        };
    }

    [Audited]
    public virtual async Task MarkMyRiskDecisionFalsePositiveAsync(Guid securityLogId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (securityLogId == Guid.Empty)
        {
            throw new SharedValidationException("Security log id is required.");
        }

        var currentUserId = _currentUser.Id ?? throw new SharedException("Current user context is unavailable.");
        var securityLog = await ApplyCurrentUserScope(unitOfWorkDbContext.IdentitySecurityLogs.AsQueryable(), currentUserId)
            .FirstOrDefaultAsync(x => x.Id == securityLogId);
        if (securityLog == null)
        {
            throw new SharedValidationException("Risk decision not found.");
        }

        if (!string.Equals(securityLog.Action, "LoginRiskBlocked", StringComparison.Ordinal))
        {
            throw new SharedValidationException("Only blocked risk decisions can be marked as false positive.");
        }

        var properties = ReadExtraProperties(securityLog.ExtraProperties);
        properties["falsePositive"] = true;
        properties["falsePositiveMarkedAtUtc"] = DateTime.UtcNow;
        properties["falsePositiveMarkedBy"] = currentUserId.ToString("D");
        securityLog.ExtraProperties = JsonSerializer.Serialize(properties);
        IdentitySecurityLogRiskMetadata.ApplyScalarFields(securityLog);

        await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = securityLog.TenantId,
            ApplicationName = "Identity",
            Identity = "Profile",
            Action = "LoginRiskFalsePositiveMarkedByUser",
            UserId = securityLog.UserId,
            UserName = securityLog.UserName,
            ClientId = securityLog.ClientId,
            ClientIpAddress = securityLog.ClientIpAddress,
            CreationTime = DateTime.UtcNow,
            ExtraProperties = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["securityLogId"] = securityLogId,
                ["markedByUser"] = currentUserId
            })
        });

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    private async Task<MfaStatusDto> BuildMfaStatusAsync(ApplicationUser user)
    {
        var recoveryCodesLeft = user.TwoFactorEnabled
            ? await _userManager.CountRecoveryCodesAsync(user)
            : 0;

        return new MfaStatusDto
        {
            IsEnabled = user.TwoFactorEnabled,
            RecoveryCodesLeft = Math.Max(0, recoveryCodesLeft)
        };
    }

    private async Task<IdentityUserNotificationSecurityPreference> GetOrCreatePreferencesAsync(ApplicationUser user)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var preferences = await unitOfWorkDbContext.IdentityUserNotificationSecurityPreferences
                    .FirstOrDefaultAsync(x => x.UserId == user.Id && x.TenantId == user.TenantId);
        if (preferences != null)
        {
            return preferences;
        }

        preferences = new IdentityUserNotificationSecurityPreference
        {
            Id = Guid.CreateVersion7(),
            TenantId = user.TenantId,
            UserId = user.Id,
            CreationTime = DateTime.UtcNow
        };

        await unitOfWorkDbContext.AddAsync(preferences);
        await unitOfWorkDbContext.SaveChangesAsync();
        return preferences;
    }

    private static UserNotificationSecurityPreferencesDto MapPreferences(IdentityUserNotificationSecurityPreference preferences)
    {
        return new UserNotificationSecurityPreferencesDto
        {
            Channels = new NotificationChannelPreferencesDto
            {
                EmailEnabled = preferences.EmailEnabled
            },
            SecurityAlerts = new SecurityAlertPreferencesDto
            {
                NewDeviceSignInAlertEnabled = preferences.NewDeviceSignInAlertEnabled,
                RiskBlockedAlertEnabled = preferences.RiskBlockedAlertEnabled,
                AdminSessionRevokedAlertEnabled = preferences.AdminSessionRevokedAlertEnabled,
                MfaChangedAlertEnabled = preferences.MfaChangedAlertEnabled
            }
        };
    }

    private static string NormalizeCode(string code)
    {
        return code.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
    }

    private static string FormatKey(string unformattedKey)
    {
        if (string.IsNullOrWhiteSpace(unformattedKey))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var current = 0;
        while (current + 4 < unformattedKey.Length)
        {
            sb.Append(unformattedKey.AsSpan(current, 4)).Append(' ');
            current += 4;
        }

        if (current < unformattedKey.Length)
        {
            sb.Append(unformattedKey[current..]);
        }

        return sb.ToString().ToLowerInvariant();
    }

    private static string GenerateQrCodeUri(ApplicationUser user, string unformattedKey)
    {
        var email = user.Email ?? user.UserName ?? user.Id.ToString("D");
        return $"otpauth://totp/{Uri.EscapeDataString(MfaIssuer)}:{Uri.EscapeDataString(email)}?secret={Uri.EscapeDataString(unformattedKey)}&issuer={Uri.EscapeDataString(MfaIssuer)}&digits=6";
    }

    private async Task SaveSecurityLogAsync(ApplicationUser user, string action)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var httpContext = _httpContextAccessor.HttpContext;
        var sessionId = httpContext?.User?.FindFirst(SharedClaimTypes.SessionId)?.Value;
        var correlationId = httpContext?.TraceIdentifier;
        var clientIp = httpContext?.Connection.RemoteIpAddress?.ToString();
        var browserInfo = httpContext?.Request.Headers.UserAgent.ToString();

        await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = user.TenantId,
            ApplicationName = "Identity",
            Identity = "Profile",
            Action = action,
            SessionId = sessionId,
            UserId = user.Id,
            UserName = user.UserName,
            ClientIpAddress = clientIp,
            BrowserInfo = string.IsNullOrWhiteSpace(browserInfo) ? null : browserInfo,
            CorrelationId = correlationId,
            CreationTime = DateTime.UtcNow
        });

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    private IQueryable<IdentitySecurityLog> ApplyCurrentUserScope(IQueryable<IdentitySecurityLog> query, Guid currentUserId)
    {
        query = query.Where(x => x.UserId == currentUserId);
        if (_currentUser.TenantId.HasValue)
        {
            query = query.Where(x => x.TenantId == _currentUser.TenantId.Value);
        }
        else
        {
            query = query.Where(x => x.TenantId == null);
        }

        return query;
    }

    private static SecurityLogDto MapSecurityLogToDto(IdentitySecurityLog log)
    {
        var riskSnapshot = IdentitySecurityLogRiskMetadata.Read(log);

        return new SecurityLogDto
        {
            Id = log.Id,
            ApplicationName = log.ApplicationName,
            Identity = log.Identity,
            Action = log.Action,
            UserId = log.UserId,
            UserName = log.UserName,
            ClientId = log.ClientId,
            ClientIpAddress = log.ClientIpAddress,
            CreationTime = log.CreationTime,
            RiskLevel = riskSnapshot.RiskLevel,
            RiskAction = riskSnapshot.RiskAction,
            RiskScore = riskSnapshot.RiskScore,
            RiskReasons = riskSnapshot.RiskReasons,
            BlockedByRisk = riskSnapshot.BlockedByRisk,
            FalsePositive = riskSnapshot.FalsePositive,
            CountryCode = riskSnapshot.CountryCode,
            RiskOverrideId = riskSnapshot.RiskOverrideId
        };
    }

    private static Dictionary<string, object?> ReadExtraProperties(string? extraPropertiesJson)
    {
        return IdentitySecurityLogRiskMetadata.ReadDictionary(extraPropertiesJson);
    }
}
