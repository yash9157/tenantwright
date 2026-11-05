using Identity.EntityFrameworkCore.EntityFrameworkCore;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
using Shared.CurrentUser;
using Shared.EventBus.Messaging;
using Identity.Application.Passkeys;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Account;

public partial class AccountAppService : IAccountAppService, ITransientDependency
{
    private const string ResetPasswordPublicPath = "/auth/reset-password";
    private const string ConfirmEmailPublicPath = "/auth/email-verification/confirm";

    private readonly ApplicationUserManager _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly IDistributedCache<IdentitySessionValidityCacheItem> _sessionValidityCache;
    private readonly IIdentityAuthCacheInvalidator _cacheInvalidator;
    private readonly ISessionMarkerStore _sessionMarkerStore;
    private readonly IOptions<SessionValidationOptions> _sessionValidationOptions;
    private readonly IIdentitySessionRevocationService _sessionRevocationService;
    private readonly ILoginRiskEvaluator _loginRiskEvaluator;
    private readonly IOptions<LoginRiskOptions> _loginRiskOptions;
    private readonly IOptions<LoginRiskAlertingOptions> _loginRiskAlertingOptions;
    private readonly IOptions<PasswordResetOptions> _passwordResetOptions;
    private readonly IOptions<EmailVerificationOptions> _emailVerificationOptions;
    private readonly IOptions<InteractiveLoginOptions> _interactiveLoginOptions;
    private readonly IOptions<LoginAttemptProtectionOptions> _loginAttemptProtectionOptions;
    private readonly IdentityUserSecurityAlertPublisher _securityAlertPublisher;
    private readonly IDistributedCache<PasswordResetQuotaState> _passwordResetQuotaCache;
    private readonly IDistributedCache<LoginAttemptQuotaState> _loginAttemptQuotaCache;
    private readonly IDistributedCache<InteractiveLoginChallengeCacheItem> _interactiveLoginChallengeCache;
    private readonly IDistributedLock _distributedLock;
    private readonly ApplicationRoleManager _roleManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly ITenantWrightMessageSender? _eventBus;
    private readonly ICurrentUser? _currentUser;
    private readonly PasskeyCeremonyService? _passkeyCeremonyService;
    private readonly ILogger<AccountAppService> _logger;

    private static readonly DistributedLockAcquireOptions QuotaLockOptions = new()
    {
        PolicyName = DistributedLockPolicyNames.ApiIdempotency,
        AcquireTimeoutMs = 250,
        LeaseMs = 3000,
        FailureMode = DistributedLockFailureMode.FailClosed,
        IgnoreTenant = true
    };

    public AccountAppService(
        ApplicationUserManager userManager, SignInManager<ApplicationUser> signInManager, IDbContextProvider<IdentityDbContextApp> dbContextProvider, IDistributedCache<IdentitySessionValidityCacheItem> sessionValidityCache, IIdentityAuthCacheInvalidator cacheInvalidator, ISessionMarkerStore sessionMarkerStore, IOptions<SessionValidationOptions> sessionValidationOptions, IIdentitySessionRevocationService sessionRevocationService, ILoginRiskEvaluator loginRiskEvaluator, IOptions<LoginRiskOptions> loginRiskOptions, IOptions<LoginRiskAlertingOptions> loginRiskAlertingOptions, IOptions<PasswordResetOptions> passwordResetOptions, IOptions<EmailVerificationOptions> emailVerificationOptions, IOptions<InteractiveLoginOptions> interactiveLoginOptions, IOptions<LoginAttemptProtectionOptions> loginAttemptProtectionOptions, IdentityUserSecurityAlertPublisher securityAlertPublisher, IDistributedCache<PasswordResetQuotaState> passwordResetQuotaCache, IDistributedCache<LoginAttemptQuotaState> loginAttemptQuotaCache, IDistributedCache<InteractiveLoginChallengeCacheItem> interactiveLoginChallengeCache, IDistributedLock distributedLock, ApplicationRoleManager roleManager, ICurrentTenant currentTenant, ITenantWrightMessageSender? eventBus = null, ICurrentUser? currentUser = null, PasskeyCeremonyService? passkeyCeremonyService = null, ILogger<AccountAppService>? logger = null)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _dbContextProvider = dbContextProvider;
        _sessionValidityCache = sessionValidityCache;
        _cacheInvalidator = cacheInvalidator;
        _sessionMarkerStore = sessionMarkerStore;
        _sessionValidationOptions = sessionValidationOptions;
        _sessionRevocationService = sessionRevocationService;
        _loginRiskEvaluator = loginRiskEvaluator;
        _loginRiskOptions = loginRiskOptions;
        _loginRiskAlertingOptions = loginRiskAlertingOptions;
        _passwordResetOptions = passwordResetOptions;
        _emailVerificationOptions = emailVerificationOptions;
        _interactiveLoginOptions = interactiveLoginOptions;
        _loginAttemptProtectionOptions = loginAttemptProtectionOptions;
        _securityAlertPublisher = securityAlertPublisher;
        _passwordResetQuotaCache = passwordResetQuotaCache;
        _loginAttemptQuotaCache = loginAttemptQuotaCache;
        _interactiveLoginChallengeCache = interactiveLoginChallengeCache;
        _distributedLock = distributedLock;
        _roleManager = roleManager;
        _currentTenant = currentTenant;
        _eventBus = eventBus;
        _currentUser = currentUser;
        _passkeyCeremonyService = passkeyCeremonyService;
        _logger = logger ?? NullLogger<AccountAppService>.Instance;
    }

    private async Task<ApplicationUser?> FindUserAsync(string loginInput)
    {
        // Try finding by Email
        var user = await _userManager.FindByEmailAsync(loginInput);
        if (user != null)
        {
            return user;
        }

        // Try finding by Phone Number
        user = await _userManager.Users.FirstOrDefaultAsync(u => u.PhoneNumber == loginInput);
        if (user != null)
        {
            return user;
        }

        // Try finding by Username
        user = await _userManager.FindByNameAsync(loginInput);
        return user;
    }

    private async Task<ApplicationUser?> FindUserInTenantAsync(Guid tenantId, string loginInput)
    {
        return await FindUserInPartitionAsync(tenantId, loginInput);
    }

    private async Task<ApplicationUser?> FindHostUserAsync(string loginInput)
    {
        return await FindUserInPartitionAsync(Guid.Empty, loginInput);
    }

    private async Task<ApplicationUser?> FindUserInPartitionAsync(Guid tenantId, string loginInput)
    {
        var normalizedEmail = _userManager.NormalizeEmail(loginInput);
        var normalizedUserName = _userManager.NormalizeName(loginInput);

        return await _userManager.Users.IgnoreQueryFilters().FirstOrDefaultAsync(user =>
            user.TenantId == tenantId &&
            (user.NormalizedEmail == normalizedEmail ||
             user.PhoneNumber == loginInput ||
             user.NormalizedUserName == normalizedUserName));
    }

    private async Task<ApplicationUser?> FindInteractiveUserAsync(
        InteractiveLoginMode mode,
        Guid? tenantId,
        string loginInput)
    {
        return mode switch
        {
            InteractiveLoginMode.Tenant when tenantId.HasValue && tenantId.Value != Guid.Empty
                => await FindUserInTenantAsync(tenantId.Value, loginInput),
            InteractiveLoginMode.Platform when !tenantId.HasValue
                => await FindHostUserAsync(loginInput),
            _ => null
        };
    }

    private static Guid? NormalizeUserTenantId(ApplicationUser user)
        => user.TenantId == Guid.Empty ? null : user.TenantId;

    private bool IsValidInteractiveContext(InteractiveLoginMode mode, Guid? tenantId)
    {
        return mode switch
        {
            InteractiveLoginMode.Tenant
                => tenantId.HasValue &&
                   tenantId.Value != Guid.Empty &&
                   _currentTenant.Id == tenantId.Value,
            InteractiveLoginMode.Platform
                => !tenantId.HasValue && !_currentTenant.Id.HasValue,
            _ => false
        };
    }

    private static string BuildLoginPartition(InteractiveLoginMode mode, Guid? tenantId)
    {
        return mode switch
        {
            InteractiveLoginMode.Tenant when tenantId.HasValue && tenantId.Value != Guid.Empty => $"tenant:{tenantId.Value:D}",
            InteractiveLoginMode.Platform when !tenantId.HasValue => "host",
            _ => "invalid"
        };
    }

    private static bool RequiresEmailActivationBeforeLogin(ApplicationUser user)
    {
        return user.RequireEmailActivationBeforeLogin && !user.EmailConfirmed;
    }

    private async Task<IdentityPublicInvitation?> FindActiveInvitationAsync(string rawToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tokenHash = ComputeSha256Hex(rawToken);
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return null;
        }

        return await unitOfWorkDbContext.IdentityPublicInvitations
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash && !x.CanceledAtUtc.HasValue);
    }

    private async Task QueueEmailVerificationAsync(
        ApplicationUser user,
        Guid tenantId,
        string safeBaseUrl,
        string templateName)
    {
        var request = await BuildEmailVerificationRequestAsync(user, tenantId, safeBaseUrl, templateName);
        var eventBus = _eventBus ?? throw new InvalidOperationException("TenantWright message sender is required to publish email verification notifications.");
        await eventBus.SendCommandAsync(request);
    }

    private async Task<NotificationSendRequestedTenantEto> BuildEmailVerificationRequestAsync(
        ApplicationUser user,
        Guid tenantId,
        string safeBaseUrl,
        string templateName,
        ApplicationUserManager? userManager = null)
    {
        var token = await (userManager ?? _userManager).GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link =
            $"{safeBaseUrl}{ConfirmEmailPublicPath}?tenantId={tenantId:D}&userId={user.Id:D}&token={Uri.EscapeDataString(encodedToken)}";

        return new NotificationSendRequestedTenantEto
        {
            RequestId = Guid.CreateVersion7(),
            TenantId = tenantId,
            OccurredAtUtc = DateTime.UtcNow,
            SourceService = "identity-service",
            DeliveryPlan = new NotificationDeliveryPlan
            {
                Mode = NotificationDeliveryMode.Parallel,
                Steps = [new NotificationChannelPlan
                    {
                        Channel = NotificationSendChannel.Email,
                        Subject = "Activate your workspace",
                        TemplateName = templateName,
                        TemplateModel = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["link"] = link,
                ["userId"] = user.Id.ToString("D"),
                ["userName"] = user.UserName
            }
                    }]
            },
            Recipients = IdentityNotificationContractPublisher.CreateSingleChannelRecipients(NotificationSendChannel.Email, [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = user.Email ?? string.Empty
                }
            ], null),
        };
    }

    private Task AddNotificationToOutboxAsync(NotificationSendRequestedTenantEto request)
    {
        var eventBus = _eventBus ?? throw new InvalidOperationException(
            "TenantWright message sender is required to publish invitation verification notifications.");
        return eventBus.SendCommandAsync(request);
    }

    private async Task<bool> TryClaimInvitationAsync(
        IdentityPublicInvitation invitation,
        Guid userId,
        DateTime claimedAtUtc)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (!unitOfWorkDbContext.IsRelational())
        {
            if (invitation.AcceptedAtUtc.HasValue ||
                invitation.CanceledAtUtc.HasValue ||
                invitation.ExpiresAtUtc <= claimedAtUtc)
            {
                return false;
            }

            invitation.AcceptedAtUtc = claimedAtUtc;
            invitation.AcceptedUserId = userId;
            return true;
        }

        var affected = await unitOfWorkDbContext.IdentityPublicInvitations
            .Where(x =>
                x.Id == invitation.Id &&
                !x.AcceptedAtUtc.HasValue &&
                !x.CanceledAtUtc.HasValue &&
                x.ExpiresAtUtc > claimedAtUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AcceptedAtUtc, claimedAtUtc)
                .SetProperty(x => x.AcceptedUserId, userId));

        if (affected != 1)
        {
            return false;
        }

        invitation.AcceptedAtUtc = claimedAtUtc;
        invitation.AcceptedUserId = userId;
        return true;
    }

    private static PublicInvitationValidateResultDto InvalidInvitationValidationResult(string message)
    {
        return new PublicInvitationValidateResultDto
        {
            Succeeded = false,
            Message = message
        };
    }

    private static PublicInvitationAcceptResultDto InvalidInvitationAcceptanceResult(string message)
    {
        return new PublicInvitationAcceptResultDto
        {
            Succeeded = false,
            Message = message
        };
    }

    private async Task<UserInfoDto> MapToUserInfoAsync(ApplicationUser user, UserInfoSegments segments)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var includeProfile = segments.HasFlag(UserInfoSegments.Profile);
        var includeAuthz = segments.HasFlag(UserInfoSegments.Authorization);

        var now = DateTimeOffset.UtcNow;
        var isLockedOut = user.LockoutEnabled
                          && user.LockoutEnd.HasValue
                          && user.LockoutEnd.Value > now;

        var roleMemberships = includeAuthz
            ? await (
                from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
                join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where userRole.UserId == user.Id &&
                      role.LifecycleState == Identity.Domain.Shared.ApplicationUsers.ApplicationRoleLifecycleState.Active
                orderby role.Id
                select new { role.Id, role.Name })
                .ToListAsync()
            : [];
        var roles = roleMemberships
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var roleIds = roleMemberships
            .Select(x => x.Id)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        var tenantId = user.TenantId == Guid.Empty ? (Guid?)null : user.TenantId;
        var tenantPartition = tenantId?.ToString("D") ?? "host";

        var profileVersion = ComputeHash(
            user.Id.ToString("D"),
            tenantPartition,
            user.UserName ?? string.Empty,
            user.Email ?? string.Empty,
            user.PhoneNumber ?? string.Empty,
            user.Name ?? string.Empty,
            user.Surname ?? string.Empty,
            user.ConcurrencyStamp ?? string.Empty);

        var lifecycleStatus = user.GetEffectiveLifecycleStatus();
        var authzVersion = ComputeHash(
            "authz:v2",
            user.Id.ToString("D"),
            tenantPartition,
            ((int)lifecycleStatus).ToString(System.Globalization.CultureInfo.InvariantCulture),
            isLockedOut ? "1" : "0",
            user.SecurityStamp ?? string.Empty,
            string.Join("|", roles),
            string.Join("|", roleIds.Select(x => x.ToString("D"))));

        return new UserInfoDto
        {
            Id = user.Id,
            UserName = includeProfile ? (user.UserName ?? string.Empty) : string.Empty,
            Email = includeProfile ? (user.Email ?? string.Empty) : string.Empty,
            PhoneNumber = includeProfile ? user.PhoneNumber : null,
            TenantId = tenantId,
            Roles = roles,
            RoleIds = roleIds,
            IsActive = lifecycleStatus == IdentityUserLifecycleStatus.Active,
            IsLockedOut = isLockedOut,
            ProfileVersion = profileVersion,
            AuthzVersion = authzVersion
        };
    }

    private static string ComputeHash(params string[] segments)
    {
        var payload = JsonSerializer.Serialize(segments.Select(x => x ?? string.Empty));
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static void NormalizeInteractiveStartInput(InteractiveLoginStartInput input)
    {
        input.LoginInput = NormalizeRequiredInput(input.LoginInput, 256);
        input.SessionId = NormalizeOptionalInput(input.SessionId, 64);
        input.ClientId = NormalizeOptionalInput(input.ClientId, 128);
        input.ClientIpAddress = NormalizeOptionalInput(input.ClientIpAddress, 64);
        input.BrowserInfo = NormalizeOptionalInput(input.BrowserInfo, 1024);
        input.CorrelationId = NormalizeOptionalInput(input.CorrelationId, 128);
        input.Device = NormalizeOptionalInput(input.Device, 128);
        input.Location = NormalizeOptionalInput(input.Location, 128);
        input.DeviceFingerprint = NormalizeOptionalInput(input.DeviceFingerprint, 1024);
    }

    private static void NormalizeInteractiveChallengeInput(InteractiveChallengeInput input)
    {
        input.SessionId = NormalizeOptionalInput(input.SessionId, 64);
        input.ClientId = NormalizeOptionalInput(input.ClientId, 128);
        input.ClientIpAddress = NormalizeOptionalInput(input.ClientIpAddress, 64);
        input.BrowserInfo = NormalizeOptionalInput(input.BrowserInfo, 1024);
        input.CorrelationId = NormalizeOptionalInput(input.CorrelationId, 128);
        input.Device = NormalizeOptionalInput(input.Device, 128);
        input.Location = NormalizeOptionalInput(input.Location, 128);
        input.DeviceFingerprint = NormalizeOptionalInput(input.DeviceFingerprint, 1024);
    }

    private static void NormalizePasswordResetRequestInput(PasswordResetRequestInput input)
    {
        input.LoginInput = NormalizeRequiredInput(input.LoginInput, 256);
        input.ResetBaseUrl = NormalizeRequiredInput(input.ResetBaseUrl, 256);
        input.ClientIpAddress = NormalizeOptionalInput(input.ClientIpAddress, 64);
        input.BrowserInfo = NormalizeOptionalInput(input.BrowserInfo, 1024);
        input.CorrelationId = NormalizeOptionalInput(input.CorrelationId, 128);
    }

    private static void NormalizePasswordResetValidateInput(PasswordResetValidateInput input)
    {
        input.Token = NormalizeRequiredInput(input.Token, 2048);
    }

    private static void NormalizePasswordResetCompleteInput(PasswordResetCompleteInput input)
    {
        NormalizePasswordResetValidateInput(input);
    }

    private static void NormalizePasswordExpiredChangeInput(PasswordExpiredChangeInput input)
    {
        input.CurrentPassword = NormalizeRequiredInput(input.CurrentPassword, 256);
        input.NewPassword = NormalizeRequiredInput(input.NewPassword, 256);
        input.ConfirmPassword = NormalizeRequiredInput(input.ConfirmPassword, 256);
    }

    private static void NormalizeEmailVerificationRequestInput(EmailVerificationRequestInput input)
    {
        input.LoginInput = NormalizeRequiredInput(input.LoginInput, 256);
        input.VerificationBaseUrl = NormalizeRequiredInput(input.VerificationBaseUrl, 256);
        input.ClientIpAddress = NormalizeOptionalInput(input.ClientIpAddress, 64);
        input.BrowserInfo = NormalizeOptionalInput(input.BrowserInfo, 1024);
        input.CorrelationId = NormalizeOptionalInput(input.CorrelationId, 128);
    }

    private static void NormalizeEmailVerificationConfirmInput(EmailVerificationConfirmInput input)
    {
        input.Token = NormalizeRequiredInput(input.Token, 2048);
    }

    private static string NormalizeRequiredInput(string? value, int maxLength)
    {
        return NormalizeOptionalInput(value, maxLength) ?? string.Empty;
    }

    private static string? NormalizeOptionalInput(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength];
    }

    private static string? NormalizeTwoFactorCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
    }

    private static string? NormalizeRecoveryCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private async Task SaveSecurityLogAsync(
        string identity,
        string action,
        string? sessionId,
        string? userName,
        Guid? userId,
        Guid? tenantId,
        string? clientId,
        string? clientIpAddress,
        string? browserInfo,
        string? correlationId,
        string? location,
        Dictionary<string, object?>? extraProperties = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var log = new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ApplicationName = "Identity",
            Identity = identity,
            Action = action,
            SessionId = NormalizeOptionalInput(sessionId, 64),
            UserId = userId,
            UserName = NormalizeOptionalInput(userName, 256),
            ClientId = NormalizeOptionalInput(clientId, 128),
            ClientIpAddress = NormalizeOptionalInput(clientIpAddress, 64),
            BrowserInfo = NormalizeOptionalInput(browserInfo, 1024),
            CorrelationId = NormalizeOptionalInput(correlationId, 128),
            Location = NormalizeOptionalInput(location, 128),
            ExtraProperties = extraProperties is { Count: > 0 }
                        ? JsonSerializer.Serialize(extraProperties)
                        : null,
            CreationTime = DateTime.UtcNow
        };

        IdentitySecurityLogRiskMetadata.ApplyScalarFields(log);

        await unitOfWorkDbContext.AddAsync(log);
    }

    private async Task UpsertTrustedDeviceAsync(
        Guid userId,
        Guid? tenantId,
        string? deviceFingerprint,
        int trustedDeviceDays)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userSecret = await EnsureDeviceSecretAsync(userId);
        var deviceHash = ComputeDeviceHash(deviceFingerprint, userSecret);
        if (string.IsNullOrWhiteSpace(deviceHash))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var expiresAtUtc = now.AddDays(Math.Max(1, trustedDeviceDays));

        var existing = await unitOfWorkDbContext.IdentityTrustedDevices.FirstOrDefaultAsync(
            x => x.UserId == userId && x.TenantId == tenantId && x.DeviceHash == deviceHash);

        if (existing == null)
        {
            await unitOfWorkDbContext.AddAsync(new IdentityTrustedDevice
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserId = userId,
                DeviceHash = deviceHash,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                ExpiresAtUtc = expiresAtUtc
            });
            return;
        }

        existing.LastSeenUtc = now;
        existing.ExpiresAtUtc = expiresAtUtc;
    }

    private async Task<bool> HasTrustedDeviceAsync(Guid userId, Guid? tenantId, string? deviceFingerprint)
    {
        // A missing DeviceSecret means the user has never registered a trusted device.
        // We load it explicitly rather than via navigation so we don't alter query paths.
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userSecret = await unitOfWorkDbContext.Users
                    .Where(u => u.Id == userId)
                    .Select(u => u.DeviceSecret)
                    .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(userSecret))
        {
            return false;
        }

        var deviceHash = ComputeDeviceHash(deviceFingerprint, userSecret);
        if (string.IsNullOrWhiteSpace(deviceHash))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        return await unitOfWorkDbContext.IdentityTrustedDevices.AnyAsync(
            x => x.UserId == userId &&
                 x.TenantId == tenantId &&
                 x.DeviceHash == deviceHash &&
                 x.ExpiresAtUtc > now);
    }

    /// <summary>
    /// Returns the user's DeviceSecret, seeding it atomically on first use.
    /// The secret is a 32-byte random value stored as hex (64 chars).
    /// It never leaves the server and is mixed into every trusted-device hash
    /// via HMAC-SHA256 so a leaked client fingerprint cannot be replayed.
    /// </summary>
    private async Task<string> EnsureDeviceSecretAsync(Guid userId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        if (user == null)
        {
            // Should not happen — caller already verified the user exists.
            throw new InvalidOperationException($"User {userId} not found when seeding DeviceSecret.");
        }

        if (!string.IsNullOrWhiteSpace(user.DeviceSecret))
        {
            return user.DeviceSecret;
        }

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        user.DeviceSecret = secret;
        // SaveChanges is called by the caller's unit-of-work after this path completes.
        return secret;
    }

    /// <summary>
    /// Computes HMAC-SHA256(userSecret, fingerprint) as a lowercase hex string.
    /// Returns null when either input is absent — treated as "no trusted device".
    /// </summary>
    private static string? ComputeDeviceHash(string? deviceFingerprint, string? userSecret)
    {
        if (string.IsNullOrWhiteSpace(deviceFingerprint) || string.IsNullOrWhiteSpace(userSecret))
        {
            return null;
        }

        var normalized = deviceFingerprint.Trim();
        if (normalized.Length > 1024)
        {
            normalized = normalized[..1024];
        }

        var keyBytes = Encoding.UTF8.GetBytes(userSecret);
        var msgBytes = Encoding.UTF8.GetBytes(normalized);
        var hash = HMACSHA256.HashData(keyBytes, msgBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<bool> IsPrivilegedUserAsync(Guid userId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var elevatedRoleNames = new[]
                {
            TenantWrightIdentityRoleNames.HostAdmin.ToUpperInvariant(),
            TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
        };

        return await (
            from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
            join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            where userRole.UserId == userId && elevatedRoleNames.Contains(role.NormalizedName ?? string.Empty)
            select userRole.UserId)
            .AnyAsync();
    }

    private static Dictionary<string, object?>? BuildRiskExtraProperties(
        LoginRiskEvaluationResult? riskEvaluation,
        string? deviceFingerprint,
        string riskMode,
        bool blockedByRisk,
        bool? mfaEnrolled)
    {
        if (riskEvaluation == null && string.IsNullOrWhiteSpace(deviceFingerprint))
        {
            return null;
        }

        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["riskMode"] = riskMode,
            ["blockedByRisk"] = blockedByRisk,
            ["mfaEnrolled"] = mfaEnrolled
        };

        if (riskEvaluation != null)
        {
            dictionary["riskLevel"] = riskEvaluation.Level.ToString();
            dictionary["riskAction"] = riskEvaluation.Action.ToString();
            dictionary["riskScore"] = riskEvaluation.Score;
            dictionary["riskReasons"] = riskEvaluation.Reasons;
            dictionary["countryCode"] = riskEvaluation.CountryCode;
            dictionary["riskOverrideId"] = riskEvaluation.AppliedOverrideId?.ToString("D");
        }

        var fingerprintHash = ComputeSha256Hex(deviceFingerprint);
        if (!string.IsNullOrWhiteSpace(fingerprintHash))
        {
            dictionary["deviceFingerprintHash"] = fingerprintHash;
        }

        return dictionary;
    }

    private static string? ComputeSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > 1024)
        {
            normalized = normalized[..1024];
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static InteractiveLoginStep ResolveInteractiveStep(LoginResultDto loginResult)
    {
        if (loginResult.RequiresMfaEnrollment)
        {
            return InteractiveLoginStep.RequiresMfaEnrollment;
        }

        if (loginResult.BlockedByRisk)
        {
            return InteractiveLoginStep.Blocked;
        }

        if (loginResult.IsLockedOut)
        {
            return InteractiveLoginStep.Locked;
        }

        if (loginResult.ShouldChangePasswordOnNextLogin)
        {
            return InteractiveLoginStep.PasswordExpired;
        }

        if (loginResult.Succeeded)
        {
            return InteractiveLoginStep.Succeeded;
        }

        if (loginResult.RequiresTwoFactor)
        {
            return InteractiveLoginStep.RequiresMfa;
        }

        return InteractiveLoginStep.Failed;
    }

    private static bool RequiresInteractiveChallenge(InteractiveLoginStep step)
    {
        return step is InteractiveLoginStep.RequiresMfa
            or InteractiveLoginStep.RequiresMfaEnrollment
            or InteractiveLoginStep.RequiresTrustDevice
            or InteractiveLoginStep.PasswordExpired;
    }

    private InteractiveLoginChallengeCacheItem CreateChallengeFromStartInput(
        InteractiveLoginStartInput input,
        InteractiveLoginStep expectedStep)
    {
        var challengeTtl = TimeSpan.FromSeconds(Math.Clamp(_interactiveLoginOptions.Value.ChallengeTtlSeconds, 30, 900));
        return new InteractiveLoginChallengeCacheItem
        {
            ChallengeId = Guid.NewGuid(),
            Mode = input.Mode,
            TenantId = input.TenantId,
            ExpectedStep = expectedStep,
            LoginInput = input.LoginInput,
            SessionId = input.SessionId,
            ClientId = input.ClientId,
            ClientIpAddress = input.ClientIpAddress,
            BrowserInfo = input.BrowserInfo,
            CorrelationId = input.CorrelationId,
            Device = input.Device,
            Location = input.Location,
            DeviceFingerprint = input.DeviceFingerprint,
            PromptForTrustDevice = input.PromptForTrustDevice,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(challengeTtl)
        };
    }

    private InteractiveLoginChallengeCacheItem CreateChallengeFromExisting(
        InteractiveLoginChallengeCacheItem source,
        InteractiveLoginStep expectedStep)
    {
        var challengeTtl = TimeSpan.FromSeconds(Math.Clamp(_interactiveLoginOptions.Value.ChallengeTtlSeconds, 30, 900));
        return new InteractiveLoginChallengeCacheItem
        {
            ChallengeId = Guid.NewGuid(),
            Mode = source.Mode,
            TenantId = source.TenantId,
            ExpectedStep = expectedStep,
            LoginInput = source.LoginInput,
            SessionId = source.SessionId,
            ClientId = source.ClientId,
            ClientIpAddress = source.ClientIpAddress,
            BrowserInfo = source.BrowserInfo,
            CorrelationId = source.CorrelationId,
            Device = source.Device,
            Location = source.Location,
            DeviceFingerprint = source.DeviceFingerprint,
            PromptForTrustDevice = source.PromptForTrustDevice,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(challengeTtl)
        };
    }

    private async Task SaveInteractiveChallengeAsync(InteractiveLoginChallengeCacheItem challenge, CancellationToken cancellationToken)
    {
        await _interactiveLoginChallengeCache.SetAsync(
            BuildInteractiveChallengeCacheKey(challenge.ChallengeId),
            challenge,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = challenge.ExpiresAtUtc
            },
            token: cancellationToken);

        LoginRiskMetrics.RecordInteractiveChallengeCreated();
    }

    private async Task<InteractiveLoginChallengeCacheItem?> ResolveInteractiveChallengeAsync(
        InteractiveChallengeInput input,
        InteractiveLoginStep expectedStep,
        CancellationToken cancellationToken)
    {
        if (input.ChallengeId.HasValue && input.ChallengeId.Value != Guid.Empty)
        {
            var cacheKey = BuildInteractiveChallengeCacheKey(input.ChallengeId.Value);
            var cached = await _interactiveLoginChallengeCache.GetAsync(cacheKey, token: cancellationToken);
            if (cached == null)
            {
                LoginRiskMetrics.RecordInteractiveChallengeReplayed();
                return null;
            }

            await _interactiveLoginChallengeCache.RemoveAsync(cacheKey, token: cancellationToken);
            if (cached.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                LoginRiskMetrics.RecordInteractiveChallengeExpired();
                return null;
            }

            if (cached.Mode != input.Mode || cached.TenantId != input.TenantId)
            {
                LoginRiskMetrics.RecordInteractiveChallengeReplayed();
                return null;
            }

            if (!InteractiveChallengeStepGuard.MatchesExpectedStep(cached, expectedStep))
            {
                LoginRiskMetrics.RecordInteractiveChallengeReplayed();
                return null;
            }

            LoginRiskMetrics.RecordInteractiveChallengeConsumed();
            return cached;
        }

        return null;
    }

    private async Task<InteractiveLoginChallengeCacheItem?> ResolveInteractiveChallengeAsync(
        PasswordExpiredChangeInput input,
        InteractiveLoginStep expectedStep,
        CancellationToken cancellationToken)
    {
        if (!input.ChallengeId.HasValue || input.ChallengeId.Value == Guid.Empty)
        {
            return null;
        }

        var cacheKey = BuildInteractiveChallengeCacheKey(input.ChallengeId.Value);
        var cached = await _interactiveLoginChallengeCache.GetAsync(cacheKey, token: cancellationToken);
        if (cached == null)
        {
            LoginRiskMetrics.RecordInteractiveChallengeReplayed();
            return null;
        }

        await _interactiveLoginChallengeCache.RemoveAsync(cacheKey, token: cancellationToken);

        if (cached.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            LoginRiskMetrics.RecordInteractiveChallengeExpired();
            return null;
        }

        if (cached.Mode != input.Mode || cached.TenantId != input.TenantId)
        {
            LoginRiskMetrics.RecordInteractiveChallengeReplayed();
            return null;
        }

        if (!InteractiveChallengeStepGuard.MatchesExpectedStep(cached, expectedStep))
        {
            LoginRiskMetrics.RecordInteractiveChallengeReplayed();
            return null;
        }

        LoginRiskMetrics.RecordInteractiveChallengeConsumed();
        return cached;
    }

    private static string BuildInteractiveChallengeCacheKey(Guid challengeId)
    {
        return $"identity:interactive:challenge:{challengeId:D}";
    }

    private static string? BuildInteractiveMessage(InteractiveLoginStep step)
    {
        return step switch
        {
            InteractiveLoginStep.RequiresMfa => "Two-factor authentication is required.",
            InteractiveLoginStep.RequiresMfaEnrollment => "A phishing-resistant factor must be enrolled before this privileged account can sign in.",
            InteractiveLoginStep.RequiresTrustDevice => "Confirm whether this device should be trusted.",
            InteractiveLoginStep.PasswordExpired => "Password change is required.",
            InteractiveLoginStep.Locked => "User account is locked.",
            InteractiveLoginStep.Blocked => "Login blocked due to high-risk sign-in.",
            InteractiveLoginStep.Failed => "Invalid credentials or tenant.",
            _ => null
        };
    }

    private async Task<bool> TryConsumeLoginAttemptQuotaAsync(
        InteractiveLoginMode mode,
        Guid? tenantId,
        string loginInput,
        string? clientIpAddress,
        string? deviceFingerprint,
        LoginAttemptProtectionOptions options,
        CancellationToken cancellationToken)
    {
        var loginHash = ComputeSha256Hex(loginInput) ?? "na";
        var ipHash = ComputeSha256Hex(clientIpAddress) ?? "na";
        var deviceHash = ComputeSha256Hex(deviceFingerprint) ?? "na";
        var partition = BuildLoginPartition(mode, tenantId);
        var key = $"identity:login:quota:{partition}:{loginHash}:{ipHash}:{deviceHash}";
        var lockName = $"identity:lock:{key}";

        try
        {
            await using var handle = await _distributedLock.TryAcquireAsync(lockName, QuotaLockOptions, cancellationToken);
            if (handle == null)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var existing = await _loginAttemptQuotaCache.GetAsync(key, token: cancellationToken);
            LoginAttemptQuotaState state;
            if (existing == null)
            {
                state = new LoginAttemptQuotaState
                {
                    WindowStartUtc = now,
                    Count = 1
                };
            }
            else
            {
                state = existing;

                var age = now - state.WindowStartUtc;
                if (age.TotalSeconds > Math.Max(1, options.RequestLimitWindowSeconds))
                {
                    state.WindowStartUtc = now;
                    state.Count = 1;
                }
                else
                {
                    state.Count++;
                }
            }

            await _loginAttemptQuotaCache.SetAsync(
                key,
                state,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, options.RequestLimitWindowSeconds))
                },
                token: cancellationToken);

            return state.Count <= Math.Max(1, options.RequestLimitPerWindow);
        }
        catch
        {
            // Fail closed on quota lock/counter errors for auth-sensitive paths.
            return false;
        }
    }

    private async Task<bool> TryConsumePasswordResetQuotaAsync(
        Guid tenantId,
        string loginInput,
        string? clientIpAddress,
        PasswordResetOptions options,
        CancellationToken cancellationToken)
    {
        var loginHash = ComputeSha256Hex(loginInput) ?? "na";
        var ipHash = ComputeSha256Hex(clientIpAddress) ?? "na";
        var key = $"identity:pwdreset:quota:{tenantId:D}:{loginHash}:{ipHash}";
        var lockName = $"identity:lock:{key}";

        try
        {
            await using var handle = await _distributedLock.TryAcquireAsync(lockName, QuotaLockOptions, cancellationToken);
            if (handle == null)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var existing = await _passwordResetQuotaCache.GetAsync(key, token: cancellationToken);
            PasswordResetQuotaState state;
            if (existing == null)
            {
                state = new PasswordResetQuotaState
                {
                    WindowStartUtc = now,
                    Count = 1
                };
            }
            else
            {
                state = existing;

                var age = now - state.WindowStartUtc;
                if (age.TotalSeconds > Math.Max(1, options.RequestLimitWindowSeconds))
                {
                    state.WindowStartUtc = now;
                    state.Count = 1;
                }
                else
                {
                    state.Count++;
                }
            }

            await _passwordResetQuotaCache.SetAsync(
                key,
                state,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, options.RequestLimitWindowSeconds))
                },
                token: cancellationToken);

            return state.Count <= Math.Max(1, options.RequestLimitPerWindow);
        }
        catch
        {
            // Fail closed on quota lock/counter errors for auth-sensitive paths.
            return false;
        }
    }

    private static string? NormalizeAndValidatePublicBaseUrl(string? value, List<string> allowedBaseUrls)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = NormalizeAbsoluteUrl(value);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (allowedBaseUrls == null || allowedBaseUrls.Count == 0)
        {
            return null;
        }

        foreach (var allowed in allowedBaseUrls)
        {
            var normalizedAllowed = NormalizeAbsoluteUrl(allowed);
            if (string.IsNullOrWhiteSpace(normalizedAllowed))
            {
                continue;
            }

            if (string.Equals(candidate, normalizedAllowed, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? NormalizeAbsoluteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static string DecodeResetToken(string token)
    {
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            var decoded = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(decoded) ? token : decoded;
        }
        catch
        {
            return token;
        }
    }

    public sealed class PasswordResetQuotaState
    {
        public DateTimeOffset WindowStartUtc { get; set; }
        public int Count { get; set; }
    }

    public sealed class LoginAttemptQuotaState
    {
        public DateTimeOffset WindowStartUtc { get; set; }
        public int Count { get; set; }
    }

    private async Task PublishRiskBlockedAlertAsync(
        ApplicationUser user,
        string? clientIpAddress,
        string? clientId,
        LoginRiskEvaluationResult riskEvaluation)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var alertOptions = _loginRiskAlertingOptions.Value;
        if (!alertOptions.Enabled)
        {
            return;
        }

        var hostAdminRoleName = TenantWrightIdentityRoleNames.HostAdmin.ToUpperInvariant();
        var hostAdminEmails = await (
            from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
            join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            join hostAdmin in unitOfWorkDbContext.Users.AsNoTracking() on userRole.UserId equals hostAdmin.Id
            where role.NormalizedName == hostAdminRoleName &&
                  hostAdmin.TenantId == Guid.Empty &&
                  hostAdmin.IsActive &&
                  hostAdmin.LifecycleStatus == IdentityUserLifecycleStatus.Active &&
                  hostAdmin.EmailConfirmed &&
                  hostAdmin.Email != null &&
                  hostAdmin.Email != string.Empty
            select hostAdmin.Email!)
            .Distinct()
            .ToListAsync();

        var operatorEmails = alertOptions.Recipients
            .Concat(hostAdminEmails)
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(email => email.Trim())
            .Where(email => !string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (operatorEmails.Count > 0)
        {
            var request = new NotificationSendRequestedHostEto
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
                            Subject = alertOptions.Subject,
                            TemplateName = alertOptions.TemplateName,
                            TemplateModel = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["userId"] = user.Id.ToString("D"),
                    ["userName"] = user.UserName,
                    ["riskLevel"] = riskEvaluation.Level.ToString(),
                    ["riskAction"] = riskEvaluation.Action.ToString(),
                    ["riskScore"] = riskEvaluation.Score.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                    ["riskReasons"] = string.Join(",", riskEvaluation.Reasons),
                    ["clientIpAddress"] = clientIpAddress,
                    ["clientId"] = clientId
                }
                        }]
                },
                Recipients = IdentityNotificationContractPublisher.CreateSingleChannelRecipients(NotificationSendChannel.Email, operatorEmails.Select(email => new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = email
                }).ToList(), null),
            };

            var eventBus = _eventBus ?? throw new InvalidOperationException("Distributed event bus is required to publish risk notifications.");
            Guid? tenantId = user.TenantId == Guid.Empty ? null : user.TenantId;
            await IdentityNotificationContractPublisher.PublishAsync(eventBus, tenantId, request);
        }

        await _securityAlertPublisher.PublishRiskBlockedAlertAsync(
            user.TenantId,
            user.Id,
            user.UserName,
            user.Email,
            clientIpAddress,
            clientId,
            riskEvaluation,
            DateTime.UtcNow);
    }

    private async Task<string?> UpsertIdentitySessionAsync(
        Guid userId,
        Guid? tenantId,
        string? sessionId,
        string? clientId,
        string? clientIpAddress,
        string? browserInfo,
        string? device,
        string? location)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var sessionGuid = Guid.TryParse(sessionId, out var parsedSessionId) ? parsedSessionId : Guid.NewGuid();
        var canonicalSessionId = sessionGuid.ToString("D");
        var now = DateTime.UtcNow;
        var existing = await unitOfWorkDbContext.IdentitySessions.FirstOrDefaultAsync(x => x.Id == sessionGuid || x.SessionId == canonicalSessionId);
        if (existing == null)
        {
            var cap = _sessionValidationOptions.Value.MaxConcurrentSessions;
            if (cap > 0)
            {
                var activeSessions = await unitOfWorkDbContext.IdentitySessions
                    .Where(x => x.UserId == userId && x.TenantId == tenantId && x.Status == IdentitySessionStatus.Active)
                    .OrderBy(x => x.LastActiveTime)
                    .ToListAsync();

                if (activeSessions.Count >= cap)
                {
                    var oldestSessionsToRevoke = activeSessions
                        .Take(activeSessions.Count - cap + 1)
                        .Select(x => x.Id)
                        .ToList();

                    await _sessionRevocationService.RevokeSelectedAsync(
                        oldestSessionsToRevoke,
                        action: "ConcurrentSessionCapEviction",
                        revocationSource: "ConcurrentSessionCap",
                        revokedByUserId: userId,
                        revocationReason: "Concurrent session limit exceeded.");
                }
            }

            await unitOfWorkDbContext.AddAsync(new IdentitySession
            {
                Id = sessionGuid,
                SessionId = canonicalSessionId,
                TenantId = tenantId,
                UserId = userId,
                DeviceId = device,
                IpAddress = clientIpAddress,
                UserAgent = browserInfo,
                CreationTime = now,
                LastActiveTime = now,
                Status = IdentitySessionStatus.Active,
                ClientId = clientId,
                Location = location
            });
        }
        else if (existing.Status == IdentitySessionStatus.Revoked)
        {
            throw new SharedValidationException("Revoked sessions cannot be reactivated.");
        }
        else
        {
            existing.TenantId = tenantId;
            existing.UserId = userId;
            existing.DeviceId = device;
            existing.IpAddress = clientIpAddress;
            existing.UserAgent = browserInfo;
            existing.LastActiveTime = now;
            existing.Status = IdentitySessionStatus.Active;
            existing.RevokedAtUtc = null;
            existing.RevokedByUserId = null;
            existing.RevocationSource = null;
            existing.RevocationReason = null;
            existing.ClientId = clientId;
            existing.Location = location;
        }

        return canonicalSessionId;
    }
}
