using Identity.Application.ApplicationUsers;
using Identity.Application.Caching;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Caching;
using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Identity.Application.Contracts.Mutations;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Data.Exceptions;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Platform;

internal class PlatformStaffAppService : IPlatformStaffAppService, ITransientDependency
{
    private static readonly Guid HostUserTenantId = Guid.Empty;

    private readonly ApplicationUserManager _userManager;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IIdentityAuthCacheInvalidator _cacheInvalidator;
    private readonly IIdentitySessionRevocationService _sessionRevocationService;

    public PlatformStaffAppService(
        ApplicationUserManager userManager, IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, ICurrentUser currentUser, IIdentityAuthCacheInvalidator cacheInvalidator, IIdentitySessionRevocationService sessionRevocationService)
    {
        _userManager = userManager;
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _cacheInvalidator = cacheInvalidator;
        _sessionRevocationService = sessionRevocationService;
    }

    public async Task<PlatformStaffDetailDto> GetAsync(string id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformActor();
        var user = await FindPlatformUserAsync(id);
        if (user == null)
        {
            throw new EntityNotFoundException<ApplicationUser>(id);
        }

        var roles = await _userManager.GetRolesAsync(user);
        var activity = (await IdentityUserActivityQuery.LoadAsync(unitOfWorkDbContext, [user.Id]))
            .GetValueOrDefault(user.Id);
        var activeSessionCount = await IdentityUserActivityQuery.CountActiveSessionsAsync(unitOfWorkDbContext, user.Id);

        var dto = new PlatformStaffDetailDto();
        FillDto(dto, user, roles, activity);
        dto.PhoneNumberConfirmed = user.PhoneNumberConfirmed;
        dto.AccessFailedCount = user.AccessFailedCount;
        dto.ShouldChangePasswordOnNextLogin = user.ShouldChangePasswordOnNextLogin;
        dto.RequireEmailActivationBeforeLogin = user.RequireEmailActivationBeforeLogin;
        dto.PendingEmail = user.PendingEmail;
        dto.PendingEmailRequestedAtUtc = user.PendingEmailRequestedAtUtc;
        dto.ActiveSessionCount = activeSessionCount;
        dto.SignInActivity = await IdentityUserActivityQuery.LoadSignInSeriesAsync(unitOfWorkDbContext, user.Id, 90);
        return dto;
    }

    public async Task<PagedResultDto<PlatformStaffDto>> GetPagedListAsync(GetPlatformStaffInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformActor();
        input ??= new GetPlatformStaffInput();

        var query = unitOfWorkDbContext.Users
            .AsNoTracking()
            .Where(x => x.TenantId == HostUserTenantId);

        query = input.LifecycleStatus.HasValue
            ? query.Where(x => x.LifecycleStatus == input.LifecycleStatus.Value)
            : query.Where(x => x.LifecycleStatus != IdentityUserLifecycleStatus.Archived);

        var filter = input.Filter?.Trim();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(x =>
                x.UserName!.Contains(filter) ||
                x.Email!.Contains(filter) ||
                (x.PhoneNumber != null && x.PhoneNumber.Contains(filter)));
        }

        if (input.IsActive.HasValue)
        {
            query = input.IsActive.Value
                ? query.Where(x => x.IsActive && x.LifecycleStatus == IdentityUserLifecycleStatus.Active)
                : query.Where(x => !x.IsActive || x.LifecycleStatus != IdentityUserLifecycleStatus.Active);
        }

        if (input.EmailConfirmed.HasValue)
        {
            query = query.Where(x => x.EmailConfirmed == input.EmailConfirmed.Value);
        }

        if (input.TwoFactorEnabled.HasValue)
        {
            query = query.Where(x => x.TwoFactorEnabled == input.TwoFactorEnabled.Value);
        }

        if (input.IsLocked.HasValue)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            query = input.IsLocked.Value
                ? query.Where(x => x.LockoutEnabled && x.LockoutEnd != null && x.LockoutEnd > nowUtc)
                : query.Where(x => !x.LockoutEnabled || x.LockoutEnd == null || x.LockoutEnd <= nowUtc);
        }

        var roleFilter = NormalizeAllowedStaffRole(input.RoleName);
        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            var normalizedRoleName = roleFilter.ToUpperInvariant();
            var userIdsInRole = from userRole in unitOfWorkDbContext.UserRoles
                                join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
                                where role.TenantId == null && role.NormalizedName == normalizedRoleName
                                select userRole.UserId;

            query = query.Where(x => userIdsInRole.Contains(x.Id));
        }

        query = ApplyActivityRangeFilters(unitOfWorkDbContext, query, input);
        // Rejects an undeclared field instead of silently falling back to the default order, so a
        // caller learns its sort was refused rather than receiving a differently-ordered page.
        var validatedSorting = SortingValidator.ValidateAndNormalize<GetPlatformStaffInput>(input.Sorting);
        query = ApplySorting(unitOfWorkDbContext, query, validatedSorting);
        var totalCount = await query.LongCountAsync();
        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var items = await query
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();
        var rolesByUserId = await LoadPlatformRolesByUserIdAsync(items.Select(x => x.Id));
        var activityByUserId = await IdentityUserActivityQuery.LoadAsync(unitOfWorkDbContext, items.Select(x => x.Id).ToList());

        return new PagedResultDto<PlatformStaffDto>
        {
            TotalCount = totalCount,
            Items = items
                .Select(user => MapToDto(
                    user,
                    rolesByUserId.GetValueOrDefault(user.Id, []),
                    activityByUserId.GetValueOrDefault(user.Id)))
                .ToList()
        };
    }

    [Audited]
    public async Task<PlatformStaffDto> CreateAsync(PlatformStaffCreateDto input)
    {
        EnsurePlatformActor();
        var roleNames = NormalizeAllowedStaffRoles(input.RoleNames);
        await EnsurePlatformRolesExistAsync(roleNames);

        var user = new ApplicationUser
        {
            UserName = input.UserName,
            Email = input.Email,
            Name = IdentityProfileName.Normalize(input.Name, "Name"),
            Surname = IdentityProfileName.Normalize(input.Surname, "Surname"),
            PhoneNumber = input.PhoneNumber,
            IsActive = input.IsActive,
            TenantId = HostUserTenantId
        };
        user.InitializeLifecycle(input.IsActive);

        var result = await _userManager.CreateAsync(user, input.Password);
        if (!result.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        if (roleNames.Count > 0)
        {
            var addRolesResult = await _userManager.AddToRolesAsync(user, roleNames);
            if (!addRolesResult.Succeeded)
            {
                await _userManager.DeleteAsync(user);
                throw new SharedValidationException(string.Join(", ", addRolesResult.Errors.Select(e => e.Description)));
            }
        }

        await InvalidatePlatformUserProfileAsync(user);
        await _cacheInvalidator.InvalidateAuthorizationAsync(null, user.Id);

        return await MapToDtoAsync(user);
    }

    [Audited]
    public async Task<PlatformStaffDto> UpdateAsync(string id, PlatformStaffUpdateDto input)
    {
        EnsurePlatformActor();
        var user = await FindPlatformUserAsync(id);
        if (user == null)
        {
            throw new EntityNotFoundException<ApplicationUser>(id);
        }

        EnsureConcurrencyStamp(user, input.ConcurrencyStamp);
        var roleNames = NormalizeAllowedStaffRoles(input.RoleNames);
        await EnsurePlatformRolesExistAsync(roleNames);
        var currentRoles = TenantWrightIdentityRoleNames.NormalizeMany(await _userManager.GetRolesAsync(user));
        if (input.IsActive != user.IsActive)
        {
            throw new SharedValidationException(
                "Profile update cannot activate or deactivate platform staff. Use the focused activate/deactivate command.");
        }

        if (!RoleSetsEqual(currentRoles, roleNames))
        {
            throw new SharedValidationException(
                "Profile update cannot change platform staff roles. Use the focused roles command.");
        }

        var originalAliasHashes = IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber);
        var originalEmail = user.Email;
        var originalPhone = user.PhoneNumber;
        var originalName = user.Name;
        var originalSurname = user.Surname;

        user.Email = input.Email;
        user.Name = IdentityProfileName.Normalize(input.Name, "Name");
        user.Surname = IdentityProfileName.Normalize(input.Surname, "Surname");
        user.PhoneNumber = input.PhoneNumber;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        var currentAliasHashes = IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber);
        var aliasHashes = originalAliasHashes.Concat(currentAliasHashes).Distinct(StringComparer.Ordinal).ToList();
        var aliasProfileChanged =
            !string.Equals(originalEmail, user.Email, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(originalPhone, user.PhoneNumber, StringComparison.OrdinalIgnoreCase);
        var displayNameChanged =
            !string.Equals(originalName, user.Name, StringComparison.Ordinal) ||
            !string.Equals(originalSurname, user.Surname, StringComparison.Ordinal);

        if (aliasProfileChanged || displayNameChanged)
        {
            await _cacheInvalidator.InvalidateProfileAsync(null, user.Id);
        }

        if (aliasProfileChanged)
        {
            await _cacheInvalidator.InvalidateAliasesAsync(null, aliasHashes);
        }

        return await MapToDtoAsync(user);
    }

    [Audited]
    public async Task<PlatformStaffDto> SetRolesAsync(string id, IdentityRoleAssignmentInput input)
    {
        EnsurePlatformActor();
        var user = await FindPlatformUserAsync(id);
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);
        EnsureConcurrencyStamp(user, input.ConcurrencyStamp);
        var reason = ValidateMutationReason(input.ReasonCode, input.Note);
        var requestedRoles = NormalizeAllowedStaffRoles(input.RoleNames);
        await EnsurePlatformRolesExistAsync(requestedRoles);
        var currentRoles = TenantWrightIdentityRoleNames.NormalizeMany(await _userManager.GetRolesAsync(user));
        var rolesToRemove = currentRoles.Except(requestedRoles, StringComparer.OrdinalIgnoreCase).ToList();
        var rolesToAdd = requestedRoles.Except(currentRoles, StringComparer.OrdinalIgnoreCase).ToList();

        if (rolesToRemove.Count > 0)
        {
            var removeResult = await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
            if (!removeResult.Succeeded) throw new SharedValidationException(string.Join(", ", removeResult.Errors.Select(e => e.Description)));
        }

        if (rolesToAdd.Count > 0)
        {
            var addResult = await _userManager.AddToRolesAsync(user, rolesToAdd);
            if (!addResult.Succeeded) throw new SharedValidationException(string.Join(", ", addResult.Errors.Select(e => e.Description)));
        }

        if (rolesToRemove.Count > 0 || rolesToAdd.Count > 0)
        {
            await _cacheInvalidator.InvalidateAuthorizationAsync(null, user.Id);
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                null,
                action: "PlatformStaffRolesChanged",
                revocationSource: "FocusedPlatformStaffRoleCommand",
                revokedByUserId: _currentUser.Id,
                revocationReason: reason);
            await WriteSecurityLogAsync("PlatformStaffRolesChanged", user, reason, input.Note);
        }

        return await MapToDtoAsync(user);
    }

    [Audited]
    public Task<PlatformStaffDto> ActivateAsync(string id, IdentityActiveStateChangeInput input)
        => TransitionLifecycleAsync(
            id,
            IdentityUserLifecycleStatus.Active,
            new IdentityLifecycleTransitionInput
            {
                ConcurrencyStamp = input.ConcurrencyStamp,
                ReasonCode = input.ReasonCode,
                Note = input.Note,
                CorrelationId = input.CorrelationId
            },
            requiredCurrentStatus: IdentityUserLifecycleStatus.Suspended,
            securityAction: "PlatformStaffActivated");

    [Audited]
    public Task<PlatformStaffDto> DeactivateAsync(string id, IdentityActiveStateChangeInput input)
        => TransitionLifecycleAsync(
            id,
            IdentityUserLifecycleStatus.Suspended,
            new IdentityLifecycleTransitionInput
            {
                ConcurrencyStamp = input.ConcurrencyStamp,
                ReasonCode = input.ReasonCode,
                Note = input.Note,
                CorrelationId = input.CorrelationId
            },
            requiredCurrentStatus: IdentityUserLifecycleStatus.Active,
            revocationAction: "PlatformStaffDeactivated",
            securityAction: "PlatformStaffDeactivated");

    [Audited]
    public Task<PlatformStaffDto> SuspendAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(id, IdentityUserLifecycleStatus.Suspended, input);

    [Audited]
    public Task<PlatformStaffDto> RestoreAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(
            id,
            IdentityUserLifecycleStatus.Active,
            input,
            requiredCurrentStatus: IdentityUserLifecycleStatus.Suspended);

    [Audited]
    public Task<PlatformStaffDto> TerminateAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(id, IdentityUserLifecycleStatus.Terminated, input);

    [Audited]
    public Task<PlatformStaffDto> RehireAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(
            id,
            IdentityUserLifecycleStatus.Active,
            input,
            allowedCurrentStatuses:
            [
                IdentityUserLifecycleStatus.Terminated,
                IdentityUserLifecycleStatus.Archived
            ]);

    [Audited]
    public Task<PlatformStaffDto> ArchiveAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(id, IdentityUserLifecycleStatus.Archived, input);

    public async Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        string id,
        GetIdentityLifecycleHistoryInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformActor();
        var user = await FindPlatformUserAsync(id);
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);

        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 100);
        var skipCount = Math.Max(0, input.SkipCount);
        var query = unitOfWorkDbContext.IdentityUserLifecycleHistory
            .AsNoTracking()
            .Where(x => x.TenantId == null && x.UserId == user.Id);

        var totalCount = await query.LongCountAsync();
        var items = await query
            .OrderByDescending(x => x.EffectiveAtUtc)
            .ThenByDescending(x => x.CreationTime)
            .Skip(skipCount)
            .Take(maxResultCount)
            .Select(x => new IdentityUserLifecycleHistoryDto
            {
                Id = x.Id,
                TenantId = x.TenantId,
                UserId = x.UserId,
                PreviousStatus = x.PreviousStatus,
                CurrentStatus = x.CurrentStatus,
                EffectiveAtUtc = x.EffectiveAtUtc,
                ChangedAtUtc = x.CreationTime,
                ReasonCode = x.ReasonCode,
                Note = x.Note,
                ActorUserId = x.ActorUserId,
                CorrelationId = x.CorrelationId,
                IsMigrationBackfill = x.IsMigrationBackfill
            })
            .ToListAsync();

        return new PagedResultDto<IdentityUserLifecycleHistoryDto>
        {
            TotalCount = totalCount,
            Items = items
        };
    }

    private async Task<PlatformStaffDto> TransitionLifecycleAsync(
        string id,
        IdentityUserLifecycleStatus targetStatus,
        IdentityLifecycleTransitionInput input,
        IdentityUserLifecycleStatus? requiredCurrentStatus = null,
        IReadOnlyCollection<IdentityUserLifecycleStatus>? allowedCurrentStatuses = null,
        string? revocationAction = null,
        string? securityAction = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformActor();
        var user = await FindPlatformUserAsync(id);
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);
        EnsureConcurrencyStamp(user, input.ConcurrencyStamp);
        var reason = ValidateMutationReason(input.ReasonCode, input.Note);
        var correlationId = NormalizeCorrelationId(input.CorrelationId);
        var currentStatus = user.GetEffectiveLifecycleStatus();

        if (currentStatus == targetStatus)
        {
            return await MapToDtoAsync(user);
        }

        if (requiredCurrentStatus.HasValue && currentStatus != requiredCurrentStatus.Value)
        {
            throw new SharedValidationException(
                $"Lifecycle transition to {targetStatus} requires current state {requiredCurrentStatus.Value}; current state is {currentStatus}.");
        }

        if (allowedCurrentStatuses != null && !allowedCurrentStatuses.Contains(currentStatus))
        {
            throw new SharedValidationException(
                $"Lifecycle transition to {targetStatus} is not allowed from {currentStatus}.");
        }

        try
        {
            user.TransitionLifecycle(targetStatus);
        }
        catch (InvalidOperationException exception)
        {
            throw new SharedValidationException(exception.Message);
        }

        var effectiveAtUtc = DateTime.UtcNow;
        unitOfWorkDbContext.IdentityUserLifecycleHistory.Add(new IdentityUserLifecycleHistory(
            Guid.CreateVersion7(),
            tenantId: null,
            user.Id,
            currentStatus,
            targetStatus,
            effectiveAtUtc,
            reason,
            input.Note?.Trim(),
            _currentUser.Id,
            correlationId));

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded) throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));

        await _cacheInvalidator.InvalidateProfileAsync(null, user.Id);
        await _cacheInvalidator.InvalidateAuthorizationAsync(null, user.Id);
        if (targetStatus != IdentityUserLifecycleStatus.Active)
        {
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                null,
                action: revocationAction ?? $"PlatformStaffLifecycle{targetStatus}",
                revocationSource: "PlatformStaffLifecycleCommand",
                revokedByUserId: _currentUser.Id,
                revocationReason: reason);
        }

        await WriteSecurityLogAsync(
            securityAction ?? $"PlatformStaffLifecycle{targetStatus}",
            user,
            reason,
            input.Note);
        return await MapToDtoAsync(user);
    }

    [Audited]
    public async Task LockAsync(string id)
    {
        EnsurePlatformActor();
        var user = await FindPlatformUserAsync(id);
        if (user == null)
        {
            throw new EntityNotFoundException<ApplicationUser>(id);
        }

        user.LockoutEnabled = true;
        var result = await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        if (!result.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        await _cacheInvalidator.InvalidateAuthorizationAsync(null, user.Id);
        await _sessionRevocationService.RevokeAllAsync(user.Id, null, action: "PlatformStaffLocked");
    }

    [Audited]
    public async Task UnlockAsync(string id)
    {
        EnsurePlatformActor();
        var user = await FindPlatformUserAsync(id);
        if (user == null)
        {
            throw new EntityNotFoundException<ApplicationUser>(id);
        }

        var unlockResult = await _userManager.SetLockoutEndDateAsync(user, null);
        if (!unlockResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", unlockResult.Errors.Select(e => e.Description)));
        }

        var resetResult = await _userManager.ResetAccessFailedCountAsync(user);
        if (!resetResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", resetResult.Errors.Select(e => e.Description)));
        }

        await _cacheInvalidator.InvalidateAuthorizationAsync(null, user.Id);
    }

    [Audited]
    public async Task DeleteAsync(string id)
    {
        EnsurePlatformActor();
        var user = await FindPlatformUserAsync(id);
        if (user == null)
        {
            return;
        }

        var aliasHashes = IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber);
        await _sessionRevocationService.RevokeAllAsync(user.Id, null, action: "PlatformStaffDeleted");
        await _userManager.DeleteAsync(user);
        await _cacheInvalidator.InvalidateProfileAsync(null, user.Id);
        await _cacheInvalidator.InvalidateAuthorizationAsync(null, user.Id);
        await _cacheInvalidator.InvalidateAliasesAsync(null, aliasHashes);
    }

    // Optimistic-concurrency guard: reject a stale write when the caller supplied a stamp that no
    // longer matches the stored value. Omitting the stamp preserves the prior last-write-wins path.
    private static void EnsureConcurrencyStamp(ApplicationUser user, string? expectedStamp)
    {
        if (string.IsNullOrWhiteSpace(expectedStamp))
        {
            return;
        }

        if (!string.Equals(user.ConcurrencyStamp, expectedStamp, StringComparison.Ordinal))
        {
            throw new SharedDbConcurrencyException(
                "The staff user was modified by someone else. Reload the latest data and try again.");
        }
    }

    private static bool RoleSetsEqual(IEnumerable<string> left, IEnumerable<string> right)
        => new HashSet<string>(left, StringComparer.OrdinalIgnoreCase)
            .SetEquals(right);

    private static string ValidateMutationReason(string? reasonCode, string? note)
    {
        var reason = reasonCode?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 64)
        {
            throw new SharedValidationException("ReasonCode is required and must not exceed 64 characters.");
        }

        if (note?.Trim().Length > 512)
        {
            throw new SharedValidationException("Note must not exceed 512 characters.");
        }

        return reason;
    }

    private static string NormalizeCorrelationId(string? correlationId)
    {
        var normalized = correlationId?.Trim();
        if (normalized?.Length > 128)
        {
            throw new SharedValidationException("CorrelationId must not exceed 128 characters.");
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? Guid.NewGuid().ToString("N")
            : normalized;
    }

    private void EnsurePlatformActor()
    {
        if (!_currentUser.IsAuthenticated || !_currentUser.IsInRole(TenantWrightIdentityRoleNames.HostAdmin))
        {
            throw new UnauthorizedAccessException("Platform staff management requires host admin context.");
        }

        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Platform staff management is only available outside tenant context.");
        }
    }

    private async Task<ApplicationUser?> FindPlatformUserAsync(string id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (!Guid.TryParse(id, out var parsedId))
        {
            return null;
        }

        return await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == parsedId && x.TenantId == HostUserTenantId);
    }

    private async Task<PlatformStaffDto> MapToDtoAsync(ApplicationUser user)
    {
        return MapToDto(user, await _userManager.GetRolesAsync(user), activity: null);
    }

    private static PlatformStaffDto MapToDto(ApplicationUser user, IEnumerable<string> roles, UserActivitySnapshot? activity)
    {
        var dto = new PlatformStaffDto();
        FillDto(dto, user, roles, activity);
        return dto;
    }

    // Fills the shared PlatformStaffDto fields (used for both list rows and the detail superset).
    private static void FillDto(PlatformStaffDto dto, ApplicationUser user, IEnumerable<string> roles, UserActivitySnapshot? activity)
    {
        var now = DateTimeOffset.UtcNow;
        dto.Id = user.Id.ToString("D");
        dto.UserName = user.UserName!;
        dto.Email = user.Email!;
        dto.Name = user.Name;
        dto.Surname = user.Surname;
        dto.PhoneNumber = user.PhoneNumber;
        dto.LifecycleStatus = user.GetEffectiveLifecycleStatus();
        dto.IsActive = dto.LifecycleStatus == IdentityUserLifecycleStatus.Active;
        dto.TenantId = null;
        dto.IsLocked = user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value > now;
        dto.LockedUntilUtc = user.LockoutEnd;
        dto.Roles = TenantWrightIdentityRoleNames.NormalizeMany(roles).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        dto.EmailConfirmed = user.EmailConfirmed;
        dto.TwoFactorEnabled = user.TwoFactorEnabled;
        dto.HasPendingEmail = !string.IsNullOrWhiteSpace(user.PendingEmail);
        dto.ConcurrencyStamp = user.ConcurrencyStamp;
        dto.LastLoginAtUtc = activity?.LastLoginAtUtc;
        dto.LastActiveAtUtc = activity?.LastActiveAtUtc;
        dto.CreatedAtUtc = user.CreatedAtUtc;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadPlatformRolesByUserIdAsync(IEnumerable<Guid> userIds)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var roleRows = await (
            from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
            join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            where ids.Contains(userRole.UserId) && role.TenantId == null
            select new { userRole.UserId, role.Name })
            .ToListAsync();

        return roleRows
            .GroupBy(row => row.UserId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)TenantWrightIdentityRoleNames
                    .NormalizeMany(group.Select(row => row.Name!))
                    .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                    .ToList());
    }

    private async Task EnsurePlatformRolesExistAsync(IReadOnlyCollection<string> roleNames)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (roleNames.Count == 0)
        {
            return;
        }

        var normalizedRoleNames = roleNames.Select(x => x.ToUpperInvariant()).ToList();
        var existing = await unitOfWorkDbContext.Roles
            .AsNoTracking()
            .Where(x => x.TenantId == null && x.NormalizedName != null && normalizedRoleNames.Contains(x.NormalizedName))
            .Select(x => x.NormalizedName!)
            .ToListAsync();
        var missing = normalizedRoleNames.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            throw new SharedValidationException($"Platform role '{missing[0]}' was not found.");
        }
    }

    private static IReadOnlyList<string> NormalizeAllowedStaffRoles(IEnumerable<string>? roleNames)
    {
        var normalized = TenantWrightIdentityRoleNames.NormalizeMany(roleNames);
        foreach (var roleName in normalized)
        {
            EnsureAllowedStaffRole(roleName);
        }

        return normalized;
    }

    private static string? NormalizeAllowedStaffRole(string? roleName)
    {
        var normalized = TenantWrightIdentityRoleNames.Normalize(roleName);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            EnsureAllowedStaffRole(normalized);
        }

        return normalized;
    }

    private static void EnsureAllowedStaffRole(string roleName)
    {
        if (TenantWrightIdentityRoleNames.IsHostAdminRole(roleName) || TenantWrightIdentityRoleNames.IsTenantAdminRole(roleName))
        {
            throw new UnauthorizedAccessException("Platform staff APIs cannot assign reserved admin roles.");
        }
    }

    private static IQueryable<ApplicationUser> ApplyActivityRangeFilters(
IdentityDbContextApp unitOfWorkDbContext, IQueryable<ApplicationUser> query,
        GetPlatformStaffInput input)
    {
        if (input.CreatedFromUtc.HasValue)
        {
            query = query.Where(user => user.CreatedAtUtc >= input.CreatedFromUtc.Value);
        }

        if (input.CreatedToUtc.HasValue)
        {
            query = query.Where(user => user.CreatedAtUtc <= input.CreatedToUtc.Value);
        }

        var successfulLogins = unitOfWorkDbContext.IdentitySecurityLogs
            .AsNoTracking()
            .Where(log => log.UserId != null && log.Action == IdentityUserActivityQuery.LoginSuccessAction);

        if (input.LastLoginFromUtc.HasValue)
        {
            query = query.Where(user => successfulLogins.Any(log =>
                log.UserId == user.Id && log.CreationTime >= input.LastLoginFromUtc.Value));
        }

        if (input.LastLoginToUtc.HasValue)
        {
            query = query.Where(user => successfulLogins.Any(log =>
                log.UserId == user.Id && log.CreationTime <= input.LastLoginToUtc.Value) &&
                !successfulLogins.Any(log => log.UserId == user.Id && log.CreationTime > input.LastLoginToUtc.Value));
        }

        return query;
    }

    private static IQueryable<ApplicationUser> ApplySorting(IdentityDbContextApp unitOfWorkDbContext, IQueryable<ApplicationUser> query, string? sorting)
    {
        var normalized = sorting?.Trim();
        var successfulLogins = unitOfWorkDbContext.IdentitySecurityLogs
            .AsNoTracking()
            .Where(log => log.UserId != null && log.Action == IdentityUserActivityQuery.LoginSuccessAction);
        return normalized?.ToLowerInvariant() switch
        {
            // Every declared field carries both directions, and a bare field name always means
            // ascending. The previous shape was direction-incomplete — it had "email" but not
            // "email asc", and gave "created"/"lastlogin" a descending meaning while "email" meant
            // ascending — so five of ten valid combinations silently fell through to username order.
            // `SortableFieldsAttribute` on GetPlatformStaffInput is the whitelist; this maps it to
            // queries and must stay complete for every declared field.
            "username" or "username asc" => query.OrderBy(x => x.UserName),
            "username desc" => query.OrderByDescending(x => x.UserName),
            "email" or "email asc" => query.OrderBy(x => x.Email).ThenBy(x => x.UserName),
            "email desc" => query.OrderByDescending(x => x.Email).ThenBy(x => x.UserName),
            "isactive" or "isactive asc" => query.OrderBy(x => x.IsActive).ThenBy(x => x.UserName),
            "isactive desc" => query.OrderByDescending(x => x.IsActive).ThenBy(x => x.UserName),
            "created" or "created asc" => query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.UserName),
            "created desc" => query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.UserName),
            "lastlogin" or "lastlogin asc" => query.OrderBy(user => successfulLogins
                .Where(log => log.UserId == user.Id)
                .Max(log => (DateTime?)log.CreationTime)).ThenBy(x => x.UserName),
            "lastlogin desc" => query.OrderByDescending(user => successfulLogins
                .Where(log => log.UserId == user.Id)
                .Max(log => (DateTime?)log.CreationTime)).ThenBy(x => x.UserName),
            _ => query.OrderBy(x => x.UserName)
        };
    }

    private async Task InvalidatePlatformUserProfileAsync(ApplicationUser user)
    {
        await _cacheInvalidator.InvalidateProfileAsync(null, user.Id);
        await _cacheInvalidator.InvalidateAliasesAsync(
            null,
            IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber));
    }

    private async Task WriteSecurityLogAsync(
        string action,
        ApplicationUser user,
        string? reasonCode = null,
        string? note = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = null,
            ApplicationName = "Identity",
            Identity = "PlatformStaffManagement",
            Action = action,
            UserId = user.Id,
            UserName = user.UserName,
            CreationTime = DateTime.UtcNow,
            ExtraProperties = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["actorUserId"] = _currentUser.Id?.ToString("D"),
                ["actorUserName"] = _currentUser.UserName,
                ["reasonCode"] = reasonCode,
                ["note"] = note?.Trim()
            })
        });
    }
}
