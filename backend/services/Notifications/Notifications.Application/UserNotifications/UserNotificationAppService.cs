using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Notifications;
using Notifications.Application.Contracts.UserNotifications;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Shared.Domain.Entities;
using Shared;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Security;
using Shared.Permissions.Authorization;
using Shared.Permissions.Definitions.Notifications;
using System.Security.Claims;
using Shared.EntityFrameworkCore;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;

using Shared.AuditLogs.Auditing;
namespace Notifications.Application.UserNotifications;

[DisableConventionalRegistration]
public sealed class UserNotificationAppService : IUserNotificationAppService, ITransientDependency
{
    private readonly IRepository<UserNotification, long> _userNotificationRepository;
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;
    private readonly NotificationsQuerySecurityOptions _querySecurityOptions;
    private readonly IPermissionChecker _permissionChecker;
    private readonly TimeProvider _timeProvider;

    public UserNotificationAppService(
        IRepository<UserNotification, long> userNotificationRepository,
        IDbContextProvider<NotificationsDbContextApp> dbContextProvider,
        ICurrentTenant currentTenant,
        ICurrentPrincipalAccessor currentPrincipalAccessor,
        IOptions<NotificationsQuerySecurityOptions> querySecurityOptions,
        IPermissionChecker permissionChecker,
        TimeProvider timeProvider)
    {
        _userNotificationRepository = userNotificationRepository;
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _currentPrincipalAccessor = currentPrincipalAccessor;
        _querySecurityOptions = querySecurityOptions.Value;
        _permissionChecker = permissionChecker;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResultDto<UserNotificationDto>> GetInboxAsync(GetUserNotificationsInput input)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Inbox.Read);
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();

        var userNotifications = await _userNotificationRepository.WithDetailsAsync(x => x.Notification);
        var baseQuery = userNotifications
            .Where(x => x.TenantId == tenantId && x.UserId == userId);

        if (input.OnlyArchived)
        {
            baseQuery = baseQuery.Where(x => x.IsArchived);
        }
        else if (!input.IncludeArchived)
        {
            baseQuery = baseQuery.Where(x => !x.IsArchived);
        }

        if (input.OnlyUnread)
        {
            baseQuery = baseQuery.Where(x => !x.IsRead);
        }

        if (!string.IsNullOrWhiteSpace(input.Category))
        {
            var category = input.Category.Trim();
            baseQuery = baseQuery.Where(x => x.Notification.Category == category);
        }

        if (input.Priority.HasValue)
        {
            var priority = input.Priority.Value;
            baseQuery = baseQuery.Where(x => x.Notification.Priority == priority);
        }

        var totalCount = await baseQuery.LongCountAsync();

        var items = await baseQuery
            .AsNoTracking()
            .OrderByDescending(x => x.CreationTime)
            .Skip(Math.Max(0, input.SkipCount))
            .Take(Math.Clamp(input.MaxResultCount, 1, 1000))
            .Select(userNotification => new UserNotificationDto
            {
                NotificationId = userNotification.NotificationId,
                IsRead = userNotification.IsRead,
                ReadTimeUtc = userNotification.ReadTimeUtc,
                IsArchived = userNotification.IsArchived,
                ArchivedTimeUtc = userNotification.ArchivedTimeUtc,
                CreationTime = userNotification.CreationTime,
                TemplateName = userNotification.Notification.TemplateName,
                TemplateModelJson = string.Empty,
                Title = userNotification.Title
                    ?? userNotification.Notification.Subject
                    ?? userNotification.Notification.TemplateName,
                Summary = userNotification.Summary,
                Body = userNotification.Body,
                Subject = userNotification.Notification.Subject,
                Category = userNotification.Notification.Category,
                Priority = userNotification.Notification.Priority,
                ActionUrl = userNotification.Notification.ActionUrl,
                Revision = userNotification.Revision
            })
            .ToListAsync();

        return new PagedResultDto<UserNotificationDto>
        {
            TotalCount = totalCount,
            Items = items
        };
    }

    public async Task<UserNotificationDto> GetAsync(Guid notificationId)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Inbox.Read);
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();
        var userNotifications = await _userNotificationRepository.WithDetailsAsync(x => x.Notification);

        var item = await userNotifications
            .AsNoTracking()
            .Where(x =>
                x.TenantId == tenantId &&
                x.UserId == userId &&
                x.NotificationId == notificationId)
            .Select(userNotification => new UserNotificationDto
            {
                NotificationId = userNotification.NotificationId,
                IsRead = userNotification.IsRead,
                ReadTimeUtc = userNotification.ReadTimeUtc,
                IsArchived = userNotification.IsArchived,
                ArchivedTimeUtc = userNotification.ArchivedTimeUtc,
                CreationTime = userNotification.CreationTime,
                TemplateName = userNotification.Notification.TemplateName,
                TemplateModelJson = string.Empty,
                Title = userNotification.Title
                    ?? userNotification.Notification.Subject
                    ?? userNotification.Notification.TemplateName,
                Summary = userNotification.Summary,
                Body = userNotification.Body,
                Subject = userNotification.Notification.Subject,
                Category = userNotification.Notification.Category,
                Priority = userNotification.Notification.Priority,
                ActionUrl = userNotification.Notification.ActionUrl,
                Revision = userNotification.Revision
            })
            .SingleOrDefaultAsync();

        return item ?? throw new EntityNotFoundException(
            NotificationErrorCodes.UserNotificationNotFound,
            "User notification not found.");
    }


    [Audited]
    public async Task<NotificationMutationResultDto> MarkAsReadAsync(MarkUserNotificationReadInput input)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Inbox.MarkRead);
        ArgumentNullException.ThrowIfNull(input);
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();
        var idempotencyKey = NormalizeIdempotencyKey(input.IdempotencyKey);
        var requestHash = ComputeHash("mark-read", input.NotificationId.ToString("D"), input.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
        var db = await _dbContextProvider.GetDbContextAsync();
        var replay = await FindReceiptAsync(db, tenantId, userId, idempotencyKey);
        if (replay is not null)
        {
            EnsureMatchingReplay(replay, requestHash);
            return Replay<NotificationMutationResultDto>(replay);
        }

        var entity = await _userNotificationRepository.FindAsync(x =>
            x.TenantId == tenantId &&
            x.UserId == userId &&
            x.NotificationId == input.NotificationId,
            includeDetails: false);

        if (entity == null)
        {
            throw new EntityNotFoundException(
                NotificationErrorCodes.UserNotificationNotFound,
                "User notification not found.");
        }

        EnsureRevision(entity.Revision, input.ExpectedRevision);

        if (!entity.IsRead)
        {
            entity.MarkRead(_timeProvider.GetUtcNow().UtcDateTime);
            await _userNotificationRepository.UpdateAsync(entity, autoSave: false);
        }

        var result = CreateMutationResult("mark-read", entity.Revision, 1, _timeProvider.GetUtcNow().UtcDateTime);
        AddReceipt(db, tenantId, userId, idempotencyKey, "mark-read", input.NotificationId.ToString("D"), requestHash, result);
        await db.SaveChangesAsync();
        return result;
    }

    [Audited]
    public async Task<MarkAllUserNotificationsReadResultDto> MarkAllAsReadAsync(MarkAllUserNotificationsReadInput input)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Inbox.MarkRead);
        ArgumentNullException.ThrowIfNull(input);
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();
        var idempotencyKey = NormalizeIdempotencyKey(input.IdempotencyKey);
        var requestHash = ComputeHash("mark-all-read", ScopeKey(tenantId), userId);
        var db = await _dbContextProvider.GetDbContextAsync();
        var replay = await FindReceiptAsync(db, tenantId, userId, idempotencyKey);
        if (replay is not null)
        {
            EnsureMatchingReplay(replay, requestHash);
            return Replay<MarkAllUserNotificationsReadResultDto>(replay);
        }

        var unread = await _userNotificationRepository.GetListAsync(x =>
            x.TenantId == tenantId &&
            x.UserId == userId &&
            !x.IsRead &&
            !x.IsArchived);

        if (unread.Count == 0)
        {
            var empty = CreateMarkAllResult(0, 0, _timeProvider.GetUtcNow().UtcDateTime);
            AddReceipt(db, tenantId, userId, idempotencyKey, "mark-all-read", "inbox", requestHash, empty);
            await db.SaveChangesAsync();
            return empty;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var entity in unread)
        {
            entity.MarkRead(now);
        }

        await _userNotificationRepository.UpdateManyAsync(unread, autoSave: false);
        var resultingRevision = unread.Max(x => x.Revision);
        var result = CreateMarkAllResult(unread.Count, resultingRevision, now);
        AddReceipt(db, tenantId, userId, idempotencyKey, "mark-all-read", "inbox", requestHash, result);
        await db.SaveChangesAsync();
        return result;
    }

    [Audited]
    public async Task<NotificationMutationResultDto> ArchiveAsync(ArchiveUserNotificationInput input)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Inbox.Archive);
        ArgumentNullException.ThrowIfNull(input);
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();
        var idempotencyKey = NormalizeIdempotencyKey(input.IdempotencyKey);
        var requestHash = ComputeHash("archive", input.NotificationId.ToString("D"), input.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
        var db = await _dbContextProvider.GetDbContextAsync();
        var replay = await FindReceiptAsync(db, tenantId, userId, idempotencyKey);
        if (replay is not null)
        {
            EnsureMatchingReplay(replay, requestHash);
            return Replay<NotificationMutationResultDto>(replay);
        }

        var entity = await _userNotificationRepository.FindAsync(x =>
            x.TenantId == tenantId &&
            x.UserId == userId &&
            x.NotificationId == input.NotificationId,
            includeDetails: false);

        if (entity == null)
        {
            throw new EntityNotFoundException(
                NotificationErrorCodes.UserNotificationNotFound,
                "User notification not found.");
        }

        EnsureRevision(entity.Revision, input.ExpectedRevision);

        if (!entity.IsArchived)
        {
            entity.Archive(_timeProvider.GetUtcNow().UtcDateTime);
            await _userNotificationRepository.UpdateAsync(entity, autoSave: false);
        }

        var result = CreateMutationResult("archive", entity.Revision, 1, _timeProvider.GetUtcNow().UtcDateTime);
        AddReceipt(db, tenantId, userId, idempotencyKey, "archive", input.NotificationId.ToString("D"), requestHash, result);
        await db.SaveChangesAsync();
        return result;
    }

    [Audited]
    public async Task<NotificationMutationResultDto> DeleteAsync(DeleteUserNotificationInput input)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Inbox.Delete);
        ArgumentNullException.ThrowIfNull(input);
        var tenantId = _currentTenant.Id;
        var userId = GetCurrentUserId();
        var idempotencyKey = NormalizeIdempotencyKey(input.IdempotencyKey);
        var requestHash = ComputeHash("delete", input.NotificationId.ToString("D"), input.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
        var db = await _dbContextProvider.GetDbContextAsync();
        var replay = await FindReceiptAsync(db, tenantId, userId, idempotencyKey);
        if (replay is not null)
        {
            EnsureMatchingReplay(replay, requestHash);
            return Replay<NotificationMutationResultDto>(replay);
        }

        var entity = await _userNotificationRepository.FindAsync(x =>
            x.TenantId == tenantId &&
            x.UserId == userId &&
            x.NotificationId == input.NotificationId,
            includeDetails: false);

        if (entity == null)
        {
            throw new EntityNotFoundException(
                NotificationErrorCodes.UserNotificationNotFound,
                "User notification not found.");
        }

        EnsureRevision(entity.Revision, input.ExpectedRevision);

        var appliedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var result = CreateMutationResult("delete", entity.Revision + 1, 1, appliedAtUtc);
        await _userNotificationRepository.DeleteAsync(entity, autoSave: false);
        AddReceipt(db, tenantId, userId, idempotencyKey, "delete", input.NotificationId.ToString("D"), requestHash, result);
        await db.SaveChangesAsync();
        return result;
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

    private static async Task<NotificationOperationReceipt?> FindReceiptAsync(
        NotificationsDbContextApp db,
        Guid? tenantId,
        string userId,
        string idempotencyKey)
    {
        var scopeKey = ScopeKey(tenantId);
        return await db.NotificationOperationReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.ScopeKey == scopeKey &&
                x.ActorUserId == userId &&
                x.IdempotencyKey == idempotencyKey);
    }

    private static void AddReceipt<T>(
        NotificationsDbContextApp db,
        Guid? tenantId,
        string userId,
        string idempotencyKey,
        string operation,
        string targetKey,
        string requestHash,
        T result)
        where T : class
    {
        var receipt = result switch
        {
            NotificationMutationResultDto mutation => mutation.Receipt,
            MarkAllUserNotificationsReadResultDto bulk => bulk.Receipt,
            _ => throw new InvalidOperationException("Unsupported notification receipt result.")
        };
        db.NotificationOperationReceipts.Add(new NotificationOperationReceipt(
            receipt.ReceiptId,
            tenantId,
            ScopeKey(tenantId),
            userId,
            idempotencyKey,
            operation,
            targetKey,
            requestHash,
            receipt.ResultingRevision,
            receipt.AffectedCount,
            JsonSerializer.Serialize(result),
            receipt.AppliedAtUtc));
    }

    private static NotificationMutationResultDto CreateMutationResult(
        string operation,
        long revision,
        int affectedCount,
        DateTime appliedAtUtc)
    {
        var receipt = CreateReceipt(operation, revision, affectedCount, appliedAtUtc);
        return new NotificationMutationResultDto { Revision = revision, Receipt = receipt };
    }

    private static MarkAllUserNotificationsReadResultDto CreateMarkAllResult(
        int updatedCount,
        long revision,
        DateTime appliedAtUtc) => new()
        {
            UpdatedCount = updatedCount,
            Receipt = CreateReceipt("mark-all-read", revision, updatedCount, appliedAtUtc)
        };

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
            ?? throw new InvalidOperationException("Stored notification operation result is invalid.");
        switch (result)
        {
            case NotificationMutationResultDto mutation:
                mutation.Receipt.IsReplay = true;
                break;
            case MarkAllUserNotificationsReadResultDto bulk:
                bulk.Receipt.IsReplay = true;
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
                "The idempotency key was already used for a different notification action.");
        }
    }

    private static void EnsureRevision(long actual, long expected)
    {
        if (expected < 1 || actual != expected)
        {
            throw new SharedConflictException(
                "notification_revision_conflict",
                "The notification changed. Reload it before trying again.");
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
