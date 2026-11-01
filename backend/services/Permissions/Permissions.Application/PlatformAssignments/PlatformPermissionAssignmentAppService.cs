using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.PlatformAssignments;
using Permissions.Application.Grants;
using Permissions.Application.Definitions;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Permissions.Application.PlatformAssignments;

public class PlatformPermissionAssignmentAppService
    : IPlatformPermissionAssignmentAppService, ITransientDependency
{
    public const string InitialProviderVersion = "initial";

    private readonly IDbContextProvider<PermissionsDbContextApp> _dbContextProvider;
    private readonly PermissionAssignmentAppService _assignmentService;
    private readonly IIdentityPermissionTargetValidator _identityTargets;
    private readonly PermissionDefinitionIndex _definitions;
    private readonly PermissionConflictRuleService _conflictRules;
    private readonly PermissionGrantHistoryRecorder _historyRecorder;
    private readonly PermissionProviderGrantCacheSyncService _grantCacheSync;
    private readonly PermissionStampService _stampService;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;

    public PlatformPermissionAssignmentAppService(
        IDbContextProvider<PermissionsDbContextApp> dbContextProvider,
        PermissionAssignmentAppService assignmentService,
        IIdentityPermissionTargetValidator identityTargets,
        PermissionDefinitionIndex definitions,
        PermissionConflictRuleService conflictRules,
        PermissionGrantHistoryRecorder historyRecorder,
        PermissionProviderGrantCacheSyncService grantCacheSync,
        PermissionStampService stampService,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser)
    {
        _dbContextProvider = dbContextProvider;
        _assignmentService = assignmentService;
        _identityTargets = identityTargets;
        _definitions = definitions;
        _conflictRules = conflictRules;
        _historyRecorder = historyRecorder;
        _grantCacheSync = grantCacheSync;
        _stampService = stampService;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
    }

    public async Task<PlatformPermissionAssignmentDto> GetRoleAsync(
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        EnsureHostActor();
        var target = await _identityTargets.GetRoleAsync(roleId, tenantId: null, cancellationToken: cancellationToken);
        EnsureAssignableRole(target);
        var groups = await _assignmentService.GetPlatformRolePermissionsAsync(roleId, cancellationToken: cancellationToken);
        return await BuildReadResultAsync(
            PermissionAssignmentTargetType.Role,
            roleId,
            target.Name,
            PermissionProviderNames.Role,
            roleId.ToString("D"),
            groups.Groups,
            cancellationToken: cancellationToken);
    }

    public async Task<PlatformPermissionAssignmentDto> GetUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        EnsureHostActor();
        var target = await _identityTargets.GetUserAsync(userId, tenantId: null, cancellationToken: cancellationToken);
        var groups = await _assignmentService.GetPlatformUserPermissionsByIdAsync(userId, cancellationToken: cancellationToken);
        return await BuildReadResultAsync(
            PermissionAssignmentTargetType.User,
            userId,
            target.DisplayName,
            PermissionProviderNames.User,
            userId.ToString("D"),
            groups.Groups,
            cancellationToken: cancellationToken);
    }

    [Audited(Category = "permissions.grant")]
    public async Task<PermissionAssignmentUpdateResultDto> UpdateRoleAsync(
        Guid roleId,
        string idempotencyKey,
        UpdatePlatformPermissionAssignmentRequest input,
        CancellationToken cancellationToken = default)
    {
        EnsureHostActor();
        var target = await _identityTargets.GetRoleAsync(roleId, tenantId: null, cancellationToken: cancellationToken);
        EnsureAssignableRole(target);
        return await UpdateAsync(
            PermissionProviderNames.Role,
            roleId.ToString("D"),
            idempotencyKey,
            input,
            cancellationToken: cancellationToken);
    }

    [Audited(Category = "permissions.grant")]
    public async Task<PermissionAssignmentUpdateResultDto> UpdateUserAsync(
        Guid userId,
        string idempotencyKey,
        UpdatePlatformPermissionAssignmentRequest input,
        CancellationToken cancellationToken = default)
    {
        EnsureHostActor();
        await _identityTargets.GetUserAsync(userId, tenantId: null, cancellationToken: cancellationToken);
        return await UpdateAsync(
            PermissionProviderNames.User,
            userId.ToString("D"),
            idempotencyKey,
            input,
            cancellationToken: cancellationToken);
    }

    private async Task<PermissionAssignmentUpdateResultDto> UpdateAsync(
        string providerName,
        string providerKey,
        string idempotencyKey,
        UpdatePlatformPermissionAssignmentRequest input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 128)
        {
            throw new SharedValidationException("A valid Idempotency-Key header is required.");
        }

        input ??= new UpdatePlatformPermissionAssignmentRequest();
        var normalizedChanges = NormalizeAndValidateChanges(providerName, input.Changes);
        var requestHash = ComputeRequestHash(input.ExpectedProviderVersion, normalizedChanges);
        var actorUserId = _currentUser.Id ?? throw new UnauthorizedAccessException("A user subject is required.");
        var dbContext = await _dbContextProvider.GetDbContextAsync();

        var operationKey = idempotencyKey.Trim();
        var existingOperation = await dbContext.PermissionAssignmentOperations
            .SingleOrDefaultAsync(x => x.TenantId == null &&
                                       x.ActorUserId == actorUserId &&
                                       x.ProviderName == providerName &&
                                       x.ProviderKey == providerKey &&
                                       x.IdempotencyKey == operationKey,
                cancellationToken: cancellationToken);
        if (existingOperation != null)
        {
            if (!string.Equals(existingOperation.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw Conflict(PermissionAssignmentConflictCode.IdempotencyKeyReused);
            }

            if (existingOperation.Status == PermissionAssignmentOperationStatus.Completed &&
                !string.IsNullOrWhiteSpace(existingOperation.ResultPayload))
            {
                return JsonSerializer.Deserialize<PermissionAssignmentUpdateResultDto>(existingOperation.ResultPayload)
                       ?? throw new InvalidOperationException("The stored assignment result is invalid.");
            }

            throw Conflict(PermissionAssignmentConflictCode.IdempotencyOperationInProgress);
        }

        var state = await dbContext.PermissionAssignmentStates
            .SingleOrDefaultAsync(x => x.TenantId == null &&
                                       x.ProviderName == providerName &&
                                       x.ProviderKey == providerKey,
                cancellationToken: cancellationToken);
        var currentVersion = state?.Version.ToString("D") ?? InitialProviderVersion;
        if (!string.Equals(input.ExpectedProviderVersion?.Trim(), currentVersion, StringComparison.Ordinal))
        {
            throw Conflict(PermissionAssignmentConflictCode.ProviderVersionConflict, currentVersion);
        }

        var existingGrants = await dbContext.PermissionGrants
            .Where(x => x.TenantId == null &&
                        x.ProviderName == providerName &&
                        x.ProviderKey == providerKey)
            .ToListAsync(cancellationToken);
        var grantByName = existingGrants.ToDictionary(x => x.Name, StringComparer.Ordinal);
        await ValidateConflictsAsync(normalizedChanges, existingGrants, cancellationToken: cancellationToken);

        var pendingRequests = await dbContext.PermissionGrantChangeRequests
            .Where(x => x.TenantId == null &&
                        x.ProviderName == providerName &&
                        x.ProviderKey == providerKey &&
                        x.Status == PermissionGrantChangeRequestStatus.Pending)
            .ToListAsync(cancellationToken);
        var pendingByName = pendingRequests.ToDictionary(x => x.PermissionName, StringComparer.Ordinal);

        var result = new PermissionAssignmentUpdateResultDto();
        var operation = new PermissionAssignmentOperation(
            Guid.CreateVersion7(),
            null,
            actorUserId,
            providerName,
            providerKey,
            operationKey,
            requestHash,
            DateTime.UtcNow);
        dbContext.PermissionAssignmentOperations.Add(operation);

        var effectiveChange = false;
        foreach (var change in normalizedChanges)
        {
            var definition = _definitions.TryGet(change.Name, out var resolvedDefinition)
                ? resolvedDefinition
                : throw new SharedValidationException($"Unknown permission: {change.Name}");

            if (definition.RequiresApproval && change.IsGranted && change.Effect == PermissionGrantEffect.Allow &&
                (!grantByName.TryGetValue(change.Name, out var currentAllow) || currentAllow.Effect != PermissionGrantEffect.Allow))
            {
                if (!pendingByName.TryGetValue(change.Name, out var pending))
                {
                    pending = new PermissionGrantChangeRequest(
                        Guid.CreateVersion7(), null, change.Name, providerName, providerKey,
                        PermissionGrantEffect.Allow, change.ExpiresAtUtc, change.Reason, change.TicketId,
                        actorUserId, DateTime.UtcNow);
                    dbContext.PermissionGrantChangeRequests.Add(pending);
                    pendingByName[change.Name] = pending;
                    effectiveChange = true;
                }

                result.PendingApproval.Add(ToPendingResult(change, pending.Id));
                continue;
            }

            if (!change.IsGranted)
            {
                if (!grantByName.TryGetValue(change.Name, out var existing))
                {
                    result.Unchanged.Add(ToResult(change));
                    continue;
                }

                dbContext.PermissionGrants.Remove(existing);
                await _historyRecorder.RecordAsync(
                    null, providerName, providerKey, change.Name,
                    PermissionGrantHistoryChangeType.Revoked,
                    existing.Effect, null, null, change.Reason, change.TicketId,
                    cancellationToken: cancellationToken);
                result.Applied.Add(ToResult(change));
                effectiveChange = true;
                continue;
            }

            if (grantByName.TryGetValue(change.Name, out var current))
            {
                var unchanged = current.Effect == change.Effect &&
                                current.ExpiresAtUtc == change.ExpiresAtUtc &&
                                string.Equals(current.Reason, change.Reason, StringComparison.Ordinal) &&
                                string.Equals(current.TicketId, change.TicketId, StringComparison.Ordinal);
                if (unchanged)
                {
                    result.Unchanged.Add(ToResult(change));
                    continue;
                }

                var oldEffect = current.Effect;
                current.SetEffect(change.Effect);
                current.SetLifecycleMetadata(change.ExpiresAtUtc, change.Reason, change.TicketId, actorUserId);
                await _historyRecorder.RecordAsync(
                    null, providerName, providerKey, change.Name,
                    oldEffect == change.Effect
                        ? PermissionGrantHistoryChangeType.Granted
                        : PermissionGrantHistoryChangeType.EffectChanged,
                    oldEffect, change.Effect, change.ExpiresAtUtc, change.Reason, change.TicketId,
                    cancellationToken: cancellationToken);
            }
            else
            {
                dbContext.PermissionGrants.Add(new PermissionGrant(
                    Guid.CreateVersion7(), null, change.Name, providerName, providerKey, change.Effect,
                    DateTime.UtcNow, change.ExpiresAtUtc, change.Reason, change.TicketId, actorUserId));
                await _historyRecorder.RecordAsync(
                    null, providerName, providerKey, change.Name,
                    PermissionGrantHistoryChangeType.Granted,
                    null, change.Effect, change.ExpiresAtUtc, change.Reason, change.TicketId,
                    cancellationToken: cancellationToken);
            }

            result.Applied.Add(ToResult(change));
            effectiveChange = true;
        }

        if (state == null)
        {
            state = new PermissionAssignmentState(
                Guid.CreateVersion7(), null, providerName, providerKey, DateTime.UtcNow);
            dbContext.PermissionAssignmentStates.Add(state);
        }
        else if (effectiveChange)
        {
            state.Advance(DateTime.UtcNow);
        }

        result.ProviderVersion = state.Version.ToString("D");
        operation.Complete(JsonSerializer.Serialize(result), DateTime.UtcNow);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (SharedConflictException ex) when (ex.Code is
            SharedErrorCodes.UniqueConstraintViolation or
            SharedErrorCodes.TransactionConflict or
            SharedErrorCodes.ConcurrencyConflict)
        {
            throw Conflict(PermissionAssignmentConflictCode.ConcurrentWriteConflict, currentVersion);
        }

        if (effectiveChange)
        {
            var stamp = await _stampService.GetNextStampAsync(null, providerName, providerKey, cancellationToken: cancellationToken);
            await _grantCacheSync.SyncAsync(null, providerName, providerKey, stamp, cancellationToken: cancellationToken);
            await _stampService.BumpAsync(null, providerName, providerKey, stamp, cancellationToken: cancellationToken);
        }

        return result;
    }

    private async Task<PlatformPermissionAssignmentDto> BuildReadResultAsync(
        PermissionAssignmentTargetType targetType,
        Guid targetId,
        string displayName,
        string providerName,
        string providerKey,
        List<Contracts.PermissionManagement.PermissionGroupDto> groups,
        CancellationToken cancellationToken)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var state = await dbContext.PermissionAssignmentStates
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == null &&
                                       x.ProviderName == providerName && x.ProviderKey == providerKey,
                cancellationToken: cancellationToken);
        var pending = await dbContext.PermissionGrantChangeRequests
            .AsNoTracking()
            .Where(x => x.TenantId == null && x.ProviderName == providerName &&
                        x.ProviderKey == providerKey && x.Status == PermissionGrantChangeRequestStatus.Pending)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new PendingPermissionAssignmentDto
            {
                RequestId = x.Id,
                PermissionName = x.PermissionName,
                Effect = x.RequestedEffect,
                RequestedAtUtc = x.CreatedAtUtc,
                ExpiresAtUtc = x.ExpiresAtUtc,
                Reason = x.Reason,
                TicketId = x.TicketId
            })
            .ToListAsync(cancellationToken);

        return new PlatformPermissionAssignmentDto
        {
            Target = new PermissionAssignmentTargetDto
            {
                Type = targetType,
                Id = targetId,
                DisplayName = displayName
            },
            ProviderVersion = state?.Version.ToString("D") ?? InitialProviderVersion,
            Groups = groups,
            PendingApprovals = pending
        };
    }

    private List<PlatformPermissionAssignmentChangeDto> NormalizeAndValidateChanges(
        string providerName,
        List<PlatformPermissionAssignmentChangeDto>? changes)
    {
        if (changes == null || changes.Count == 0)
        {
            throw new SharedValidationException("At least one permission change is required.");
        }

        var result = new List<PlatformPermissionAssignmentChangeDto>(changes.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in changes)
        {
            var name = source.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                throw new SharedValidationException("Permission changes must contain unique, non-empty names.");
            }

            if (!_definitions.TryGet(name, out var definition) || !definition.IsEnabled)
            {
                throw new SharedValidationException($"Permission is unknown or disabled: {name}");
            }

            var allowedProviders = definition.Providers.Count == 0
                ? [PermissionProviderNames.Role, PermissionProviderNames.User]
                : definition.Providers.Select(PermissionProviderNameNormalizer.Normalize).ToArray();
            if (!allowedProviders.Contains(providerName, StringComparer.Ordinal))
            {
                throw new SharedValidationException($"Permission provider is not allowed: {name}");
            }

            if (source.ExpiresAtUtc.HasValue && source.ExpiresAtUtc <= DateTime.UtcNow)
            {
                throw new SharedValidationException($"Permission expiry must be in the future: {name}");
            }

            var reason = NormalizeOptional(source.Reason, PermissionGrantConsts.MaxReasonLength, "reason");
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new SharedValidationException($"A reason is required for permission changes: {name}");
            }

            result.Add(new PlatformPermissionAssignmentChangeDto
            {
                Name = name,
                IsGranted = source.IsGranted,
                Effect = source.Effect,
                ExpiresAtUtc = source.ExpiresAtUtc,
                Reason = reason,
                TicketId = NormalizeOptional(source.TicketId, PermissionGrantConsts.MaxTicketIdLength, "ticket")
            });
        }

        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    private async Task ValidateConflictsAsync(
        IReadOnlyCollection<PlatformPermissionAssignmentChangeDto> changes,
        IReadOnlyCollection<PermissionGrant> existingGrants,
        CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(
            existingGrants.Where(x => x.Effect == PermissionGrantEffect.Allow).Select(x => x.Name),
            StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!change.IsGranted || change.Effect != PermissionGrantEffect.Allow)
            {
                held.Remove(change.Name);
            }
        }

        var conflictMap = await _conflictRules.BuildConflictMapAsync(null, cancellationToken: cancellationToken);
        foreach (var change in changes.Where(x => x.IsGranted && x.Effect == PermissionGrantEffect.Allow))
        {
            var conflict = PermissionConflictRuleService.FindConflict(conflictMap, change.Name, held);
            if (conflict != null)
            {
                throw Conflict(
                    PermissionAssignmentConflictCode.AssignmentConflict,
                    conflictingNames: [change.Name, conflict]);
            }
            held.Add(change.Name);
        }
    }

    private void EnsureHostActor()
    {
        if (_currentTenant.Id.HasValue || !_currentUser.IsAuthenticated ||
            (!_currentUser.IsInRole("admin") && !_currentUser.IsInRole("HostAdmin")))
        {
            throw new UnauthorizedAccessException("Platform permission assignments require host administrator context.");
        }
    }

    private static void EnsureAssignableRole(IdentityRolePermissionTarget target)
    {
        if (target.IsStatic || !target.IsActive)
        {
            throw new UnauthorizedAccessException("The selected platform role is not assignable.");
        }
    }

    private static string ComputeRequestHash(
        string expectedVersion,
        IReadOnlyCollection<PlatformPermissionAssignmentChangeDto> changes)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            ExpectedProviderVersion = expectedVersion?.Trim(),
            Changes = changes.Select(x => new
            {
                x.Name,
                x.IsGranted,
                Effect = (int)x.Effect,
                ExpiresAtUtc = x.ExpiresAtUtc?.ToUniversalTime().ToString("O"),
                x.Reason,
                x.TicketId
            })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string? NormalizeOptional(string? value, int maximumLength, string fieldName)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maximumLength)
        {
            throw new SharedValidationException($"Permission {fieldName} exceeds {maximumLength} characters.");
        }
        return normalized;
    }

    private static PermissionAssignmentChangeResultDto ToResult(PlatformPermissionAssignmentChangeDto change)
        => new()
        {
            PermissionName = change.Name,
            IsGranted = change.IsGranted,
            Effect = change.Effect,
            ExpiresAtUtc = change.ExpiresAtUtc,
            Reason = change.Reason,
            TicketId = change.TicketId
        };

    private static PendingPermissionAssignmentChangeResultDto ToPendingResult(
        PlatformPermissionAssignmentChangeDto change,
        Guid requestId)
        => new()
        {
            RequestId = requestId,
            PermissionName = change.Name,
            IsGranted = change.IsGranted,
            Effect = change.Effect,
            ExpiresAtUtc = change.ExpiresAtUtc,
            Reason = change.Reason,
            TicketId = change.TicketId
        };

    private static PlatformPermissionAssignmentConflictException Conflict(
        PermissionAssignmentConflictCode code,
        string? currentVersion = null,
        IReadOnlyCollection<string>? conflictingNames = null)
        => new(new PermissionAssignmentConflictDto
        {
            Code = code,
            CurrentProviderVersion = currentVersion,
            ConflictingPermissionNames = conflictingNames?.Distinct(StringComparer.Ordinal).ToList() ?? []
        });
}
