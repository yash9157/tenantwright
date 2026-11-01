using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Policy;
using Notifications.Domain.Policy;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Security;
using System.Security.Claims;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Permissions.Authorization;
using Shared.Permissions.Definitions.Notifications;
namespace Notifications.Application.Policy;

[DisableConventionalRegistration]
public sealed class NotificationPolicyAppService : INotificationPolicyAppService, ITransientDependency
{
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;
    private readonly IPermissionChecker _permissionChecker;
    private readonly TimeProvider _timeProvider;
    private readonly TenantDatabaseFanOut _tenantDatabases;

    public NotificationPolicyAppService(
        IDbContextProvider<NotificationsDbContextApp> dbContextProvider,
        IDataFilter dataFilter,
        ICurrentTenant currentTenant,
        ICurrentPrincipalAccessor currentPrincipalAccessor,
        IPermissionChecker permissionChecker,
        TimeProvider timeProvider,
        TenantDatabaseFanOut tenantDatabases)
    {
        _dbContextProvider = dbContextProvider;
        _tenantDatabases = tenantDatabases;
        _dataFilter = dataFilter;
        _currentTenant = currentTenant;
        _currentPrincipalAccessor = currentPrincipalAccessor;
        _permissionChecker = permissionChecker;
        _timeProvider = timeProvider;
        _tenantDatabases = tenantDatabases;
    }

    public async Task<NotificationPolicyDto> GetTenantPolicyAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Policy.Read);
        EnsureHostTenantRequest(tenantId);
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await _tenantDatabases.FindTenantDatabaseAsync<NotificationsDbContextApp>(tenantId, cancellationToken));
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        using var scope = _dataFilter.Disable<IMultiTenant>();
        var policy = await unitOfWorkDbContext.TenantNotificationPolicies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        return Map(policy, tenantId);
    }

    public async Task<NotificationPolicyDto> UpdateTenantPolicyAsync(
        Guid tenantId,
        UpdateNotificationPolicyInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Policy.Manage);
        EnsureHostTenantRequest(tenantId);
        // Delivery reads the policy in the tenant's scope, so it is written where the tenant's data lives.
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await _tenantDatabases.FindTenantDatabaseAsync<NotificationsDbContextApp>(tenantId, cancellationToken));
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        ArgumentNullException.ThrowIfNull(input);
        var actorUserId = GetCurrentUserId();
        var idempotencyKey = NormalizeIdempotencyKey(input.IdempotencyKey);
        var requestHash = ComputeHash("update-tenant-policy", tenantId.ToString("D"), JsonSerializer.Serialize(new
        {
            input.IanaTimeZoneId,
            input.QuietHoursEnabled,
            input.QuietStartLocalMinutes,
            input.QuietEndLocalMinutes,
            input.QuietChannels,
            input.BypassCategories,
            input.DigestFrequency,
            input.ExpectedRevision
        }));
        using var scope = _dataFilter.Disable<IMultiTenant>();
        var replay = await FindReceiptAsync(unitOfWorkDbContext, tenantId, actorUserId, idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            EnsureMatchingReplay(replay, requestHash);
            return Replay<NotificationPolicyDto>(replay);
        }
        var policy = await unitOfWorkDbContext.TenantNotificationPolicies
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (policy == null)
        {
            EnsureRevision(actual: 0, input.ExpectedRevision);
            policy = new TenantNotificationPolicy(Guid.CreateVersion7(), tenantId);
            unitOfWorkDbContext.TenantNotificationPolicies.Add(policy);
        }
        else
        {
            EnsureRevision(policy.Revision, input.ExpectedRevision);
        }

        policy.Update(
            input.IanaTimeZoneId,
            input.QuietHoursEnabled,
            input.QuietStartLocalMinutes,
            input.QuietEndLocalMinutes,
            input.QuietChannels,
            input.BypassCategories,
            input.DigestFrequency);
        var result = Map(policy, tenantId);
        result.Receipt = CreateReceipt("update-tenant-policy", policy.Revision, 1, _timeProvider.GetUtcNow().UtcDateTime);
        AddReceipt(unitOfWorkDbContext, tenantId, actorUserId, idempotencyKey, "update-tenant-policy", $"tenant:{tenantId:D}", requestHash, result);
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<NotificationPolicyPreviewDto> PreviewTenantPolicyAsync(
        NotificationPolicyPreviewInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Policy.Read);
        ArgumentNullException.ThrowIfNull(input);
        EnsureHostTenantRequest(input.TenantId);
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await _tenantDatabases.FindTenantDatabaseAsync<NotificationsDbContextApp>(input.TenantId, cancellationToken));
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var atUtc = input.AtUtc == default ? _timeProvider.GetUtcNow().UtcDateTime : input.AtUtc;
        if (atUtc.Kind != DateTimeKind.Utc)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.PolicyPreviewTimeNotUtc,
                "Policy preview time must be UTC.");
        }

        using var scope = _dataFilter.Disable<IMultiTenant>();
        var policy = await unitOfWorkDbContext.TenantNotificationPolicies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == input.TenantId, cancellationToken);
        var decision = NotificationPolicyCalculator.Evaluate(
            input.Channel,
            input.Category,
            input.Priority,
            atUtc,
            policy,
            userPreference: null);
        return new NotificationPolicyPreviewDto
        {
            WillBeHeld = decision.ShouldHold,
            Bypassed = decision.Bypassed,
            Reason = decision.Reason,
            NextAllowedAtUtc = decision.NextAllowedAtUtc
        };
    }

    public async Task<UserNotificationPreferenceDto> GetMyPreferenceAsync(CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Inbox.Read);
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();
        var preference = await unitOfWorkDbContext.UserNotificationPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, cancellationToken);
        return Map(preference, tenantId);
    }

    public async Task<UserNotificationPreferenceDto> UpdateMyPreferenceAsync(
        UpdateUserNotificationPreferenceInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Inbox.Read);
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        ArgumentNullException.ThrowIfNull(input);
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();
        var idempotencyKey = NormalizeIdempotencyKey(input.IdempotencyKey);
        var requestHash = ComputeHash("update-user-preference", tenantId?.ToString("D") ?? "host", userId, JsonSerializer.Serialize(new
        {
            input.QuietHoursEnabled,
            input.IanaTimeZoneId,
            input.QuietStartLocalMinutes,
            input.QuietEndLocalMinutes,
            input.QuietChannels,
            input.DigestFrequency,
            input.ExpectedRevision
        }));
        var replay = await FindReceiptAsync(unitOfWorkDbContext, tenantId, userId, idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            EnsureMatchingReplay(replay, requestHash);
            return Replay<UserNotificationPreferenceDto>(replay);
        }
        TenantNotificationPolicy? tenantPolicy = null;
        if (tenantId.HasValue)
        {
            tenantPolicy = await unitOfWorkDbContext.TenantNotificationPolicies
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId.Value, cancellationToken);
            EnsurePreferenceNarrowsTenantPolicy(input, tenantPolicy);
        }

        var preference = await unitOfWorkDbContext.UserNotificationPreferences
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, cancellationToken);
        if (preference == null)
        {
            EnsureRevision(actual: 0, input.ExpectedRevision);
            preference = new UserNotificationPreference(Guid.CreateVersion7(), tenantId, userId);
            unitOfWorkDbContext.UserNotificationPreferences.Add(preference);
        }
        else
        {
            EnsureRevision(preference.Revision, input.ExpectedRevision);
        }

        preference.Update(
            input.QuietHoursEnabled,
            input.IanaTimeZoneId,
            input.QuietStartLocalMinutes,
            input.QuietEndLocalMinutes,
            input.QuietChannels,
            input.DigestFrequency);
        var result = Map(preference, tenantId);
        result.Receipt = CreateReceipt("update-user-preference", preference.Revision, 1, _timeProvider.GetUtcNow().UtcDateTime);
        AddReceipt(unitOfWorkDbContext, tenantId, userId, idempotencyKey, "update-user-preference", $"preference:{userId}", requestHash, result);
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    private void EnsureHostTenantRequest(Guid tenantId)
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Tenant policy management requires host context.");
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        }
    }

    private static void EnsurePreferenceNarrowsTenantPolicy(
        UpdateUserNotificationPreferenceInput input,
        TenantNotificationPolicy? policy)
    {
        if (policy == null)
        {
            if (input.DigestFrequency is not null and not NotificationDigestFrequency.Disabled)
            {
                throw new SharedConflictException(
                    NotificationErrorCodes.DigestNotAllowed,
                    "A user cannot enable digests when the tenant digest policy is disabled.");
            }

            return;
        }

        if ((input.QuietChannels & policy.QuietChannels) != policy.QuietChannels)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.QuietHoursFloorRequired,
                "A user preference cannot remove channels required by the tenant quiet-hours policy.");
        }

        if (input.DigestFrequency.HasValue && input.DigestFrequency.Value != NotificationDigestFrequency.Disabled)
        {
            var tenantFrequency = policy.DigestFrequency;
            var userFrequency = input.DigestFrequency.Value;
            var valid = tenantFrequency switch
            {
                NotificationDigestFrequency.Disabled => false,
                NotificationDigestFrequency.Daily => userFrequency is NotificationDigestFrequency.Daily or NotificationDigestFrequency.Weekly,
                NotificationDigestFrequency.Weekly => userFrequency == NotificationDigestFrequency.Weekly,
                _ => false
            };
            if (!valid)
            {
                throw new SharedConflictException(
                    NotificationErrorCodes.DigestFrequencyNotAllowed,
                    "A user digest preference may only narrow the tenant digest policy.");
            }
        }
    }

    private string GetCurrentUserId()
    {
        var principal = _currentPrincipalAccessor.Principal;
        var userId = principal.FindFirstValue(SharedClaimTypes.Subject)
            ?? principal.FindFirstValue(SharedClaimTypes.UserId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException("Missing user identity.");
        }

        return userId.Trim();
    }

    private async Task EnsurePermissionAsync(string permission)
    {
        if (!await _permissionChecker.IsGrantedAsync(_currentPrincipalAccessor.Principal, permission))
        {
            throw new SharedForbiddenException(
                "notifications_permission_required",
                "The exact notification permission is required.");
        }
    }

    private static NotificationPolicyDto Map(TenantNotificationPolicy? policy, Guid tenantId)
    {
        return new NotificationPolicyDto
        {
            TenantId = tenantId,
            IanaTimeZoneId = policy?.IanaTimeZoneId ?? "UTC",
            QuietHoursEnabled = policy?.QuietHoursEnabled ?? false,
            QuietStartLocalMinutes = policy?.QuietStartLocalMinutes ?? 0,
            QuietEndLocalMinutes = policy?.QuietEndLocalMinutes ?? 0,
            QuietChannels = policy?.QuietChannels ?? NotificationPolicyChannel.None,
            BypassCategories = policy?.GetBypassCategories().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            DigestFrequency = policy?.DigestFrequency ?? NotificationDigestFrequency.Disabled,
            LastUpdatedAtUtc = policy == null ? null : policy.LastModificationTime ?? policy.CreationTime,
            Revision = policy?.Revision ?? 0
        };
    }

    private static UserNotificationPreferenceDto Map(UserNotificationPreference? preference, Guid? tenantId)
    {
        return new UserNotificationPreferenceDto
        {
            TenantId = tenantId,
            QuietHoursEnabled = preference?.QuietHoursEnabled ?? false,
            IanaTimeZoneId = preference?.IanaTimeZoneId ?? "UTC",
            QuietStartLocalMinutes = preference?.QuietStartLocalMinutes ?? 0,
            QuietEndLocalMinutes = preference?.QuietEndLocalMinutes ?? 0,
            QuietChannels = preference?.QuietChannels ?? NotificationPolicyChannel.None,
            DigestFrequency = preference?.DigestFrequency,
            LastUpdatedAtUtc = preference == null ? null : preference.LastModificationTime ?? preference.CreationTime,
            Revision = preference?.Revision ?? 0
        };
    }

    private static async Task<NotificationOperationReceipt?> FindReceiptAsync(
        NotificationsDbContextApp db,
        Guid? tenantId,
        string actorUserId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var scopeKey = ScopeKey(tenantId);
        return await db.NotificationOperationReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ScopeKey == scopeKey &&
            x.ActorUserId == actorUserId &&
            x.IdempotencyKey == idempotencyKey,
            cancellationToken);
    }

    private static void AddReceipt<T>(
        NotificationsDbContextApp db,
        Guid? tenantId,
        string actorUserId,
        string idempotencyKey,
        string operation,
        string targetKey,
        string requestHash,
        T result)
        where T : class
    {
        var receipt = result switch
        {
            NotificationPolicyDto policy => policy.Receipt!,
            UserNotificationPreferenceDto preference => preference.Receipt!,
            _ => throw new InvalidOperationException("Unsupported notification policy result.")
        };
        db.NotificationOperationReceipts.Add(new NotificationOperationReceipt(
            receipt.ReceiptId,
            tenantId,
            ScopeKey(tenantId),
            actorUserId,
            idempotencyKey,
            operation,
            targetKey,
            requestHash,
            receipt.ResultingRevision,
            receipt.AffectedCount,
            JsonSerializer.Serialize(result),
            receipt.AppliedAtUtc));
    }

    private static NotificationOperationReceiptDto CreateReceipt(
        string operation,
        long revision,
        int affectedCount,
        DateTime appliedAtUtc) => new()
        {
            ReceiptId = Guid.CreateVersion7(),
            Operation = operation,
            ResultingRevision = revision,
            AffectedCount = affectedCount,
            AppliedAtUtc = appliedAtUtc
        };

    private static T Replay<T>(NotificationOperationReceipt receipt) where T : class
    {
        var result = JsonSerializer.Deserialize<T>(receipt.ResponseJson)
            ?? throw new InvalidOperationException("Stored notification policy result is invalid.");
        switch (result)
        {
            case NotificationPolicyDto policy:
                policy.Receipt!.IsReplay = true;
                break;
            case UserNotificationPreferenceDto preference:
                preference.Receipt!.IsReplay = true;
                break;
        }
        return result;
    }

    private static void EnsureMatchingReplay(NotificationOperationReceipt receipt, string requestHash)
    {
        if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new SharedConflictException(
                "notification_idempotency_payload_conflict",
                "The idempotency key was already used for a different policy action.");
        }
    }

    private static void EnsureRevision(long actual, long expected)
    {
        if (expected < 0 || actual != expected)
        {
            throw new SharedConflictException(
                "notification_revision_conflict",
                "The notification policy changed. Reload it before trying again.");
        }
    }

    private static string NormalizeIdempotencyKey(string value) =>
        global::Shared.Check.NotNullOrWhiteSpace(value, nameof(value), maxLength: 128).Trim();

    private static string ScopeKey(Guid? tenantId) => tenantId?.ToString("D") ?? "host";

    private static string ComputeHash(params string[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', values)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
