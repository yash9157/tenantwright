using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Application.Contracts.Users;
using Identity.Application.Contracts.Caching;
using Identity.Application.ApplicationUsers;
using Identity.Application.Caching;
using Identity.Application.Contracts.Account;
using Identity.Application.Tenancy;
using Identity.Domain.ApplicationUsers;
using Identity.Application.Contracts.Mutations;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shared;
using Shared.CurrentUser;
using Shared.Data.Exceptions;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

internal class UserAppService : IUserAppService, ITransientDependency
{
    private readonly ApplicationUserManager _userManager;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly IIdentityAuthCacheInvalidator _cacheInvalidator;
    private readonly IIdentitySessionRevocationService _sessionRevocationService;
    private readonly TenantUserManagementGuard _managementGuard;
    private readonly ICurrentUser _currentUser;
    private readonly IdentityUserImportExportCoordinator _userImportExportCoordinator;
    private readonly ITenantStore _tenantStore;

    public UserAppService(
        ApplicationUserManager userManager, IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, IIdentityAuthCacheInvalidator cacheInvalidator, IIdentitySessionRevocationService sessionRevocationService, TenantUserManagementGuard managementGuard, ICurrentUser currentUser, IdentityUserImportExportCoordinator userImportExportCoordinator, ITenantStore tenantStore)
    {
        _userManager = userManager;
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _cacheInvalidator = cacheInvalidator;
        _sessionRevocationService = sessionRevocationService;
        _managementGuard = managementGuard;
        _currentUser = currentUser;
        _userImportExportCoordinator = userImportExportCoordinator;
        _tenantStore = tenantStore;
    }

    public async Task<UserDetailDto> GetAsync(string id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId);
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);
        await _managementGuard.EnsureCanManageTargetAsync(user);

        var roles = TenantWrightIdentityRoleNames.NormalizeMany(await _userManager.GetRolesAsync(user)).ToList();
        var activity = (await IdentityUserActivityQuery.LoadAsync(unitOfWorkDbContext, [user.Id]))
            .GetValueOrDefault(user.Id);
        var activeSessionCount = await IdentityUserActivityQuery.CountActiveSessionsAsync(unitOfWorkDbContext, user.Id);

        var dto = new UserDetailDto();
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

    public async Task<PagedResultDto<UserDto>> GetPagedListAsync(GetUsersInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        input ??= new GetUsersInput();

        var tenantId = input.TenantId is null
            ? _managementGuard.RequireTenantId()
            : _managementGuard.ResolveTenantId(input.TenantId);
        using var tenantScope = _currentTenant.Change(tenantId);
        var query = unitOfWorkDbContext.Users
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId);

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

        var roleFilter = TenantWrightIdentityRoleNames.Normalize(input.RoleName);
        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            _managementGuard.EnsureCanAssignRoles([roleFilter]);
            var normalizedRoleName = roleFilter.ToUpperInvariant();
            var userIdsInRole = from userRole in unitOfWorkDbContext.UserRoles
                                join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
                                where role.TenantId == tenantId && role.NormalizedName == normalizedRoleName
                                select userRole.UserId;

            query = query.Where(x => userIdsInRole.Contains(x.Id));
        }

        query = ApplyVisibleUserScope(unitOfWorkDbContext, query, tenantId);
        query = ApplyActivityRangeFilters(unitOfWorkDbContext, query, input);
        // Rejects an undeclared field instead of silently falling back to the default order, so a
        // caller learns its sort was refused rather than receiving a differently-ordered page.
        query = ApplySorting(
            unitOfWorkDbContext,
            query,
            SortingValidator.ValidateAndNormalize<GetUsersInput>(input.Sorting));
        var totalCount = await query.LongCountAsync();
        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var items = await query
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();
        var rolesByUserId = await LoadRolesByUserIdAsync(items.Select(x => x.Id), tenantId);
        var activityByUserId = await IdentityUserActivityQuery.LoadAsync(unitOfWorkDbContext, items.Select(x => x.Id).ToList());
        var dtos = items
            .Select(user => MapToDto(
                user,
                rolesByUserId.GetValueOrDefault(user.Id, []),
                activityByUserId.GetValueOrDefault(user.Id)))
            .ToList();

        return new PagedResultDto<UserDto>
        {
            TotalCount = totalCount,
            Items = dtos
        };
    }

    public async Task<PagedResultDto<UserDto>> GetHostPagedListAsync(GetHostTenantUsersInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureHostManagementActor();
        input ??= new GetHostTenantUsersInput();

        var query = unitOfWorkDbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.TenantId != Guid.Empty);

        if (input.TenantId.HasValue)
        {
            await EnsureKnownTenantAsync(input.TenantId.Value);
            query = query.Where(x => x.TenantId == input.TenantId.Value);
        }

        query = ApplyDirectoryFilters(unitOfWorkDbContext, query, input, roleTenantId: null);
        query = ApplyActivityRangeFilters(unitOfWorkDbContext, query, input);
        // GetHostTenantUsersInput derives from GetUsersInput, so it inherits the same declaration and
        // is served by the same mapping below.
        query = ApplySorting(
            unitOfWorkDbContext,
            query,
            SortingValidator.ValidateAndNormalize<GetHostTenantUsersInput>(input.Sorting));

        var totalCount = await query.LongCountAsync();
        var items = await query
            .Skip(Math.Max(0, input.SkipCount))
            .Take(Math.Clamp(input.MaxResultCount, 1, 200))
            .ToListAsync();

        var rolesByUserId = await LoadHostRolesByUserIdAsync(items.Select(x => x.Id));
        var activityByUserId = await IdentityUserActivityQuery.LoadAcrossTenantsAsync(
            unitOfWorkDbContext,
            items.Select(x => x.Id).ToList());
        var tenantNamesById = (await _tenantStore.GetListAsync())
            .GroupBy(tenant => tenant.Id)
            .ToDictionary(group => group.Key, group => group.First().Name);

        return new PagedResultDto<UserDto>
        {
            TotalCount = totalCount,
            Items = items.Select(user =>
            {
                var dto = MapToDto(
                    user,
                    rolesByUserId.GetValueOrDefault(user.Id, []),
                    activityByUserId.GetValueOrDefault(user.Id));
                dto.TenantName = tenantNamesById.GetValueOrDefault(user.TenantId);
                return dto;
            }).ToList()
        };
    }

    public async Task<List<TenantPrimaryAdminDto>> GetPrimaryAdminsAsync(TenantPrimaryAdminsInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureHostManagementActor();

        input ??= new TenantPrimaryAdminsInput();
        var tenantIds = input.TenantIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (tenantIds.Count == 0)
        {
            return [];
        }

        var roleName = TenantWrightIdentityRoleNames.Normalize(input.RoleName) ?? TenantWrightIdentityRoleNames.TenantAdmin;
        var normalizedRoleName = roleName.ToUpperInvariant();

        // Single set-based query across all requested tenants (no per-tenant N+1).
        var rows = await (
            from user in unitOfWorkDbContext.Users.IgnoreQueryFilters().AsNoTracking()
            join userRole in unitOfWorkDbContext.UserRoles.IgnoreQueryFilters() on user.Id equals userRole.UserId
            join role in unitOfWorkDbContext.Roles.IgnoreQueryFilters() on userRole.RoleId equals role.Id
            where tenantIds.Contains(user.TenantId)
                && role.TenantId == user.TenantId
                && role.NormalizedName == normalizedRoleName
            select new
            {
                TenantId = user.TenantId,
                UserId = user.Id,
                user.UserName,
                user.Name,
                user.Surname,
                user.Email,
                user.IsPrimaryAdministrator,
                IsActive = user.IsActive && user.LifecycleStatus == IdentityUserLifecycleStatus.Active
            })
            .ToListAsync();

        // One primary admin per tenant: deterministic pick (active first, then by user name).
        return rows
            .GroupBy(row => row.TenantId)
            .Select(group =>
            {
                var primary = group
                    .OrderByDescending(x => x.IsPrimaryAdministrator && x.IsActive)
                    .ThenByDescending(x => x.IsActive)
                    .ThenBy(x => x.UserName, StringComparer.OrdinalIgnoreCase)
                    .First();
                var displayName = ComposeDisplayName(primary.Name, primary.Surname, primary.UserName);
                return new TenantPrimaryAdminDto
                {
                    TenantId = group.Key,
                    PrimaryAdminUserId = primary.UserId.ToString(),
                    PrimaryAdminName = displayName,
                    PrimaryAdminEmail = primary.Email,
                    IsActive = primary.IsActive
                };
            })
            .ToList();
    }

    [Audited]
    public async Task<TenantPrimaryAdminDto> HandoverPrimaryAdministratorAsync(
        PrimaryAdministratorHandoverInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        using var tenantScope = _currentTenant.Change(tenantId);
        if (input.TargetUserId == Guid.Empty)
        {
            throw new SharedValidationException("A target primary administrator is required.");
        }

        var reason = ValidateMutationReason(input.ReasonCode, input.Note);
        var target = await unitOfWorkDbContext.Users
            .FirstOrDefaultAsync(user => user.Id == input.TargetUserId && user.TenantId == tenantId)
            ?? throw new SharedValidationException("The selected primary administrator does not belong to this tenant.");

        await _managementGuard.EnsureCanManageTargetAsync(target);
        EnsureConcurrencyStamp(target, input.TargetConcurrencyStamp);

        if (target.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active || !target.IsActive)
        {
            throw new SharedValidationException("Only an active tenant administrator can become the primary administrator.");
        }

        var roleName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant();
        var administrators = await (
            from user in unitOfWorkDbContext.Users
            join userRole in unitOfWorkDbContext.UserRoles on user.Id equals userRole.UserId
            join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
            where user.TenantId == tenantId
                && role.TenantId == tenantId
                && role.NormalizedName == roleName
            select user)
            .Distinct()
            .ToListAsync();

        if (!administrators.Any(user => user.Id == target.Id))
        {
            throw new SharedValidationException("The selected user must already hold the tenant administrator role.");
        }

        var current = administrators
            .OrderByDescending(user => user.IsPrimaryAdministrator && user.GetEffectiveLifecycleStatus() == IdentityUserLifecycleStatus.Active)
            .ThenByDescending(user => user.GetEffectiveLifecycleStatus() == IdentityUserLifecycleStatus.Active)
            .ThenBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (input.ExpectedCurrentPrimaryAdministratorUserId != current?.Id)
        {
            throw new SharedDbConcurrencyException(
                "The primary administrator changed while you were reviewing this tenant. Reload and try again.");
        }

        if (target.IsPrimaryAdministrator && current?.Id == target.Id)
        {
            return MapPrimaryAdministrator(target);
        }

        foreach (var administrator in administrators.Where(user => user.IsPrimaryAdministrator))
        {
            administrator.IsPrimaryAdministrator = false;
        }

        // The filtered unique index permits only one primary administrator per tenant. Flush the clear first, but let
        // the ambient request UoW own the transaction and final commit.
        await unitOfWorkDbContext.SaveChangesAsync();

        target.IsPrimaryAdministrator = true;
        await WriteSecurityLogAsync("PrimaryAdministratorHandedOver", target, reason, input.Note, input.CorrelationId);
        await unitOfWorkDbContext.SaveChangesAsync();

        return MapPrimaryAdministrator(target);
    }

    [Audited]
    public async Task<BulkUserActionResultDto> ActivateBulkAsync(BulkUserActionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var users = await ResolveBulkTargetUsersAsync(input);

        foreach (var user in users)
        {
            var currentStatus = user.GetEffectiveLifecycleStatus();
            if (currentStatus == IdentityUserLifecycleStatus.Active)
            {
                continue;
            }

            if (currentStatus != IdentityUserLifecycleStatus.Suspended)
            {
                throw new SharedValidationException(
                    $"Bulk activation cannot restore a {currentStatus} user. Use the focused rehire command.");
            }

            user.TransitionLifecycle(IdentityUserLifecycleStatus.Active);
            AddLifecycleHistory(unitOfWorkDbContext, user, currentStatus, IdentityUserLifecycleStatus.Active, "bulk-activate", null, Guid.NewGuid().ToString("N"));
            await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
            await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
            await WriteSecurityLogAsync("BulkActivate", user);
        }

        await unitOfWorkDbContext.SaveChangesAsync();
        return new BulkUserActionResultDto
        {
            RequestedCount = users.Count,
            ProcessedCount = users.Count
        };
    }

    [Audited]
    public async Task<BulkUserActionResultDto> DeactivateBulkAsync(BulkUserActionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var users = await ResolveBulkTargetUsersAsync(input);

        foreach (var user in users)
        {
            var currentStatus = user.GetEffectiveLifecycleStatus();
            if (currentStatus == IdentityUserLifecycleStatus.Suspended)
            {
                continue;
            }

            if (currentStatus != IdentityUserLifecycleStatus.Active)
            {
                throw new SharedValidationException(
                    $"Bulk deactivation cannot suspend a {currentStatus} user.");
            }

            user.TransitionLifecycle(IdentityUserLifecycleStatus.Suspended);
            AddLifecycleHistory(unitOfWorkDbContext, user, currentStatus, IdentityUserLifecycleStatus.Suspended, "bulk-deactivate", null, Guid.NewGuid().ToString("N"));
            await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
            await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                user.TenantId,
                action: "UserBulkDeactivated");
            await WriteSecurityLogAsync("BulkDeactivate", user);
        }

        await unitOfWorkDbContext.SaveChangesAsync();
        return new BulkUserActionResultDto
        {
            RequestedCount = users.Count,
            ProcessedCount = users.Count
        };
    }

    [Audited]
    public async Task<BulkUserActionResultDto> RevokeSessionsBulkAsync(BulkUserActionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var users = await ResolveBulkTargetUsersAsync(input);

        foreach (var user in users)
        {
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                user.TenantId,
                action: "LogoutAllSessionsByAdmin",
                revocationSource: "BulkUserRevokeSessions",
                revokedByUserId: _currentUser.Id);
            await WriteSecurityLogAsync("BulkRevokeSessions", user);
        }

        await unitOfWorkDbContext.SaveChangesAsync();
        return new BulkUserActionResultDto
        {
            RequestedCount = users.Count,
            ProcessedCount = users.Count
        };
    }

    public Task<UserExportJobDto> CreateExportAsync(RequestUserExportInput input)
    {
        return _userImportExportCoordinator.CreateExportAsync(input);
    }

    public Task<UserExportJobDto> GetExportAsync(long id)
    {
        return _userImportExportCoordinator.GetExportAsync(id);
    }

    public Task<UserExportDownloadDto> GetExportDownloadAsync(long id, string token)
    {
        return _userImportExportCoordinator.GetExportDownloadAsync(id, token);
    }

    public Task<UserImportValidationDto> ValidateImportAsync(ValidateUserImportInput input)
    {
        return _userImportExportCoordinator.ValidateImportAsync(input);
    }

    public Task<UserImportJobDto> StartImportAsync(StartUserImportInput input)
    {
        return _userImportExportCoordinator.StartImportAsync(input);
    }

    public Task<UserImportJobDto> GetImportAsync(long id)
    {
        return _userImportExportCoordinator.GetImportAsync(id);
    }

    [Audited]
    public async Task<UserDto> CreateAsync(UserCreateDto input)
    {
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.ResolveTenantId(input.TenantId);
        using var tenantScope = _currentTenant.Change(tenantId);
        var normalizedRoleNames = _managementGuard.NormalizeRequestedRoleNames(input.RoleNames);
        _managementGuard.EnsureCanAssignRoles(normalizedRoleNames);

        var user = new ApplicationUser
        {
            UserName = input.UserName,
            Email = input.Email,
            Name = IdentityProfileName.Normalize(input.Name, "Name"),
            Surname = IdentityProfileName.Normalize(input.Surname, "Surname"),
            PhoneNumber = input.PhoneNumber,
            IsActive = input.IsActive,
            TenantId = tenantId
        };
        user.InitializeLifecycle(input.IsActive);

        var result = await _userManager.CreateAsync(user, input.Password);
        if (!result.Succeeded) throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));

        if (normalizedRoleNames.Count > 0)
        {
            var addRolesResult = await _userManager.AddToRolesAsync(user, normalizedRoleNames);
            if (!addRolesResult.Succeeded)
            {
                await _userManager.DeleteAsync(user);
                throw new SharedValidationException(string.Join(", ", addRolesResult.Errors.Select(e => e.Description)));
            }
        }

        await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        await _cacheInvalidator.InvalidateAliasesAsync(
            user.TenantId,
            IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber));

        return await MapToDto(user);
    }


    [Audited]
    public async Task<UserDto> UpdateAsync(string id, UserUpdateDto input)
    {
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId);
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);
        await _managementGuard.EnsureCanManageTargetAsync(user);
        EnsureConcurrencyStamp(user, input.ConcurrencyStamp);
        var normalizedRoleNames = _managementGuard.NormalizeRequestedRoleNames(input.RoleNames);
        _managementGuard.EnsureCanAssignRoles(normalizedRoleNames);
        var currentRoles = TenantWrightIdentityRoleNames.NormalizeMany(await _userManager.GetRolesAsync(user));
        if (input.IsActive != user.IsActive)
        {
            throw new SharedValidationException(
                "Profile update cannot activate or deactivate a user. Use the focused activate/deactivate command.");
        }

        if (!RoleSetsEqual(currentRoles, normalizedRoleNames))
        {
            throw new SharedValidationException(
                "Profile update cannot change roles. Use the focused roles command.");
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
        if (!result.Succeeded) throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));

        var currentAliasHashes = IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber);
        var mergedAliasHashes = originalAliasHashes
            .Concat(currentAliasHashes)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var aliasProfileChanged =
            !string.Equals(originalEmail, user.Email, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(originalPhone, user.PhoneNumber, StringComparison.OrdinalIgnoreCase);
        var displayNameChanged =
            !string.Equals(originalName, user.Name, StringComparison.Ordinal) ||
            !string.Equals(originalSurname, user.Surname, StringComparison.Ordinal);

        if (aliasProfileChanged || displayNameChanged)
        {
            await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
        }

        if (aliasProfileChanged)
        {
            await _cacheInvalidator.InvalidateAliasesAsync(user.TenantId, mergedAliasHashes);
        }

        return await MapToDto(user);
    }

    [Audited]
    public async Task<UserDto> SetRolesAsync(string id, IdentityRoleAssignmentInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId);
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);
        await _managementGuard.EnsureCanManageTargetAsync(user);
        EnsureConcurrencyStamp(user, input.ConcurrencyStamp);
        var reason = ValidateMutationReason(input.ReasonCode, input.Note);
        var requestedRoles = _managementGuard.NormalizeRequestedRoleNames(input.RoleNames);
        _managementGuard.EnsureCanAssignRoles(requestedRoles);
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
            await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                user.TenantId,
                action: "UserRolesChanged",
                revocationSource: "FocusedUserRoleCommand",
                revokedByUserId: _currentUser.Id,
                revocationReason: reason);
            await WriteSecurityLogAsync("RolesChanged", user, reason, input.Note);
            await unitOfWorkDbContext.SaveChangesAsync();
        }

        return await MapToDto(user);
    }

    [Audited]
    public Task<UserDto> ActivateAsync(string id, IdentityActiveStateChangeInput input)
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
            securityAction: "Activated");

    [Audited]
    public Task<UserDto> DeactivateAsync(string id, IdentityActiveStateChangeInput input)
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
            revocationAction: "UserDeactivated",
            securityAction: "Deactivated");

    [Audited]
    public Task<UserDto> SuspendAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(id, IdentityUserLifecycleStatus.Suspended, input);

    [Audited]
    public Task<UserDto> RestoreAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(
            id,
            IdentityUserLifecycleStatus.Active,
            input,
            requiredCurrentStatus: IdentityUserLifecycleStatus.Suspended);

    [Audited]
    public Task<UserDto> TerminateAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(id, IdentityUserLifecycleStatus.Terminated, input);

    [Audited]
    public Task<UserDto> RehireAsync(string id, IdentityLifecycleTransitionInput input)
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
    public Task<UserDto> ArchiveAsync(string id, IdentityLifecycleTransitionInput input)
        => TransitionLifecycleAsync(id, IdentityUserLifecycleStatus.Archived, input);

    public async Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        string id,
        GetIdentityLifecycleHistoryInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId);
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);
        await _managementGuard.EnsureCanManageTargetAsync(user);

        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 100);
        var skipCount = Math.Max(0, input.SkipCount);
        var query = unitOfWorkDbContext.IdentityUserLifecycleHistory
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.UserId == user.Id);

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

    private async Task<UserDto> TransitionLifecycleAsync(
        string id,
        IdentityUserLifecycleStatus targetStatus,
        IdentityLifecycleTransitionInput input,
        IdentityUserLifecycleStatus? requiredCurrentStatus = null,
        IReadOnlyCollection<IdentityUserLifecycleStatus>? allowedCurrentStatuses = null,
        string? revocationAction = null,
        string? securityAction = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId);
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);
        await _managementGuard.EnsureCanManageTargetAsync(user);
        EnsureConcurrencyStamp(user, input.ConcurrencyStamp);
        var reason = ValidateMutationReason(input.ReasonCode, input.Note);
        var correlationId = NormalizeCorrelationId(input.CorrelationId);
        var currentStatus = user.GetEffectiveLifecycleStatus();

        if (currentStatus == targetStatus)
        {
            return await MapToDto(user);
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
        AddLifecycleHistory(unitOfWorkDbContext, user, currentStatus, targetStatus, reason, input.Note, correlationId, effectiveAtUtc);

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded) throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));

        await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        if (targetStatus != IdentityUserLifecycleStatus.Active)
        {
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                user.TenantId,
                action: revocationAction ?? $"UserLifecycle{targetStatus}",
                revocationSource: "IdentityUserLifecycleCommand",
                revokedByUserId: _currentUser.Id,
                revocationReason: reason);
        }

        await WriteSecurityLogAsync(securityAction ?? $"Lifecycle{targetStatus}", user, reason, input.Note);
        await unitOfWorkDbContext.SaveChangesAsync();
        return await MapToDto(user);
    }

    [Audited]
    public async Task LockAsync(string id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId);
        if (user == null)
        {
            throw new EntityNotFoundException<ApplicationUser>(id);
        }

        await _managementGuard.EnsureCanManageTargetAsync(user);

        user.LockoutEnabled = true;
        var lockResult = await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        if (!lockResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", lockResult.Errors.Select(e => e.Description)));
        }

        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        await _sessionRevocationService.RevokeAllAsync(user.Id, user.TenantId, action: "UserManuallyLocked");
        await WriteSecurityLogAsync("ManualLock", user);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [Audited]
    public async Task UnlockAsync(string id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId);
        if (user == null)
        {
            throw new EntityNotFoundException<ApplicationUser>(id);
        }

        await _managementGuard.EnsureCanManageTargetAsync(user);

        var unlockResult = await _userManager.SetLockoutEndDateAsync(user, null);
        if (!unlockResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", unlockResult.Errors.Select(e => e.Description)));
        }

        var resetFailedCountResult = await _userManager.ResetAccessFailedCountAsync(user);
        if (!resetFailedCountResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", resetFailedCountResult.Errors.Select(e => e.Description)));
        }

        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        await WriteSecurityLogAsync("ManualUnlock", user);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [Audited]
    public async Task<UserDto> LockAsync(string id, IdentityLockStateChangeInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        ArgumentNullException.ThrowIfNull(input);
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId)
            ?? throw new EntityNotFoundException<ApplicationUser>(id);
        await _managementGuard.EnsureCanManageTargetAsync(user);
        EnsureConcurrencyStamp(user, input.ConcurrencyStamp);
        var reason = ValidateMutationReason(input.ReasonCode, input.Note);
        var correlationId = NormalizeCorrelationId(input.CorrelationId);

        user.LockoutEnabled = true;
        var result = await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        if (!result.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", result.Errors.Select(error => error.Description)));
        }

        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        await _sessionRevocationService.RevokeAllAsync(
            user.Id,
            user.TenantId,
            action: "UserManuallyLocked",
            revocationSource: "IdentityUserLockCommand",
            revokedByUserId: _currentUser.Id,
            revocationReason: reason);
        await WriteSecurityLogAsync("ManualLock", user, reason, input.Note, correlationId);
        await unitOfWorkDbContext.SaveChangesAsync();
        return await MapToDto(user);
    }

    [Audited]
    public async Task<UserDto> UnlockAsync(string id, IdentityLockStateChangeInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        ArgumentNullException.ThrowIfNull(input);
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId)
            ?? throw new EntityNotFoundException<ApplicationUser>(id);
        await _managementGuard.EnsureCanManageTargetAsync(user);
        EnsureConcurrencyStamp(user, input.ConcurrencyStamp);
        var reason = ValidateMutationReason(input.ReasonCode, input.Note);
        var correlationId = NormalizeCorrelationId(input.CorrelationId);

        var unlockResult = await _userManager.SetLockoutEndDateAsync(user, null);
        if (!unlockResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", unlockResult.Errors.Select(error => error.Description)));
        }

        var resetFailedCountResult = await _userManager.ResetAccessFailedCountAsync(user);
        if (!resetFailedCountResult.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", resetFailedCountResult.Errors.Select(error => error.Description)));
        }

        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        await WriteSecurityLogAsync("ManualUnlock", user, reason, input.Note, correlationId);
        await unitOfWorkDbContext.SaveChangesAsync();
        return await MapToDto(user);
    }

    [Audited]
    public async Task DeleteAsync(string id)
    {
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var user = await FindTenantUserAsync(id, tenantId);
        if (user == null) return;
        await _managementGuard.EnsureCanManageTargetAsync(user);
        var aliasHashes = IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber);

        await _sessionRevocationService.RevokeAllAsync(
            user.Id,
            user.TenantId,
            action: "UserDeleted");
        await _userManager.DeleteAsync(user);
        await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id);
        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        await _cacheInvalidator.InvalidateAliasesAsync(user.TenantId, aliasHashes);
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
                "The user was modified by someone else. Reload the latest data and try again.");
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

    private void AddLifecycleHistory(
IdentityDbContextApp unitOfWorkDbContext, ApplicationUser user,
        IdentityUserLifecycleStatus previousStatus,
        IdentityUserLifecycleStatus currentStatus,
        string reasonCode,
        string? note,
        string correlationId,
        DateTime? effectiveAtUtc = null)
    {
        unitOfWorkDbContext.IdentityUserLifecycleHistory.Add(new IdentityUserLifecycleHistory(
            Guid.CreateVersion7(),
            user.TenantId,
            user.Id,
            previousStatus,
            currentStatus,
            effectiveAtUtc ?? DateTime.UtcNow,
            reasonCode,
            note?.Trim(),
            _currentUser.Id,
            correlationId));
    }

    private async Task<ApplicationUser?> FindTenantUserAsync(string id, Guid tenantId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (!Guid.TryParse(id, out var parsedId))
        {
            return null;
        }

        return await unitOfWorkDbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == parsedId && x.TenantId == tenantId);
    }

    private async Task<UserDto> MapToDto(ApplicationUser user)
    {
        return MapToDto(user, await _userManager.GetRolesAsync(user), activity: null);
    }

    // Prefer a human display name ("Name Surname") when either name part is set;
    // fall back to the user name so tenants whose admins have no profile name
    // (the pre-existing case) keep their current projection unchanged.
    internal static string? ComposeDisplayName(string? name, string? surname, string? userName)
    {
        var composed = string.Join(' ', new[] { name, surname }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(composed) ? userName : composed;
    }

    private static UserDto MapToDto(ApplicationUser user, IEnumerable<string> roles, UserActivitySnapshot? activity)
    {
        var dto = new UserDto();
        FillDto(dto, user, roles, activity);
        return dto;
    }

    private static TenantPrimaryAdminDto MapPrimaryAdministrator(ApplicationUser user)
        => new()
        {
            TenantId = user.TenantId,
            PrimaryAdminUserId = user.Id.ToString(),
            PrimaryAdminName = ComposeDisplayName(user.Name, user.Surname, user.UserName),
            PrimaryAdminEmail = user.Email,
            IsActive = user.GetEffectiveLifecycleStatus() == IdentityUserLifecycleStatus.Active && user.IsActive
        };

    // Fills the shared UserDto fields (used for both list rows and the detail superset).
    private static void FillDto(UserDto dto, ApplicationUser user, IEnumerable<string> roles, UserActivitySnapshot? activity)
    {
        var now = DateTimeOffset.UtcNow;
        dto.Id = user.Id.ToString();
        dto.UserName = user.UserName!;
        dto.Email = user.Email!;
        dto.Name = user.Name;
        dto.Surname = user.Surname;
        dto.PhoneNumber = user.PhoneNumber;
        dto.LifecycleStatus = user.GetEffectiveLifecycleStatus();
        dto.IsActive = dto.LifecycleStatus == IdentityUserLifecycleStatus.Active;
        dto.TenantId = user.TenantId;
        dto.IsLocked = user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value > now;
        dto.LockedUntilUtc = user.LockoutEnd;
        dto.Roles = TenantWrightIdentityRoleNames.NormalizeMany(roles).ToList();
        dto.EmailConfirmed = user.EmailConfirmed;
        dto.TwoFactorEnabled = user.TwoFactorEnabled;
        dto.HasPendingEmail = !string.IsNullOrWhiteSpace(user.PendingEmail);
        dto.ConcurrencyStamp = user.ConcurrencyStamp;
        dto.LastLoginAtUtc = activity?.LastLoginAtUtc;
        dto.LastActiveAtUtc = activity?.LastActiveAtUtc;
        dto.CreatedAtUtc = user.CreatedAtUtc;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadRolesByUserIdAsync(
        IEnumerable<Guid> userIds,
        Guid tenantId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var roleRows = await (
            from userRole in unitOfWorkDbContext.UserRoles.IgnoreQueryFilters().AsNoTracking()
            join role in unitOfWorkDbContext.Roles.IgnoreQueryFilters().AsNoTracking() on userRole.RoleId equals role.Id
            where ids.Contains(userRole.UserId) && role.TenantId == tenantId
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

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadHostRolesByUserIdAsync(IEnumerable<Guid> userIds)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var roleRows = await (
            from userRole in unitOfWorkDbContext.UserRoles.IgnoreQueryFilters().AsNoTracking()
            join role in unitOfWorkDbContext.Roles.IgnoreQueryFilters().AsNoTracking() on userRole.RoleId equals role.Id
            where ids.Contains(userRole.UserId) && role.TenantId != null
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

    private async Task<List<ApplicationUser>> ResolveBulkTargetUsersAsync(BulkUserActionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();

        var normalizedIds = (input?.UserIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedIds.Count == 0)
        {
            throw new SharedValidationException("At least one user id is required.");
        }

        // Resolve every requested user in a single query instead of one round-trip per id (N+1).
        var parsedIds = new List<Guid>(normalizedIds.Count);
        foreach (var id in normalizedIds)
        {
            if (Guid.TryParse(id, out var parsed))
            {
                parsedIds.Add(parsed);
            }
        }

        var usersById = parsedIds.Count == 0
            ? new Dictionary<Guid, ApplicationUser>()
            : (await unitOfWorkDbContext.Users
                .Where(x => parsedIds.Contains(x.Id) && x.TenantId == tenantId)
                .ToListAsync())
                .ToDictionary(x => x.Id);

        var users = new List<ApplicationUser>(normalizedIds.Count);
        foreach (var id in normalizedIds)
        {
            ApplicationUser? user = Guid.TryParse(id, out var parsed) && usersById.TryGetValue(parsed, out var found)
                ? found
                : null;

            if (user == null)
            {
                throw new SharedValidationException($"User '{id}' was not found in the current tenant.");
            }

            await _managementGuard.EnsureCanManageTargetAsync(user);
            users.Add(user);
        }

        return users;
    }

    private IQueryable<ApplicationUser> ApplyDirectoryFilters(
IdentityDbContextApp unitOfWorkDbContext, IQueryable<ApplicationUser> query,
        GetUsersInput input,
        Guid? roleTenantId)
    {
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

        var roleFilter = TenantWrightIdentityRoleNames.Normalize(input.RoleName);
        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            _managementGuard.EnsureCanAssignRoles([roleFilter]);
            var normalizedRoleName = roleFilter.ToUpperInvariant();
            var userIdsInRole = from userRole in unitOfWorkDbContext.UserRoles
                                join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
                                where role.NormalizedName == normalizedRoleName &&
                                      (roleTenantId.HasValue ? role.TenantId == roleTenantId : role.TenantId != null)
                                select userRole.UserId;
            query = query.Where(x => userIdsInRole.Contains(x.Id));
        }

        return query;
    }

    private static IQueryable<ApplicationUser> ApplyActivityRangeFilters(IdentityDbContextApp unitOfWorkDbContext, IQueryable<ApplicationUser> query, GetUsersInput input)
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
            // `SortableFieldsAttribute` on GetUsersInput is the whitelist; this maps it to queries and
            // must stay complete for every declared field.
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

    private async Task EnsureKnownTenantAsync(Guid tenantId)
    {
        if (tenantId == Guid.Empty || await _tenantStore.FindAsync(tenantId) is null)
        {
            throw new SharedValidationException("The target tenant does not exist or is unavailable.");
        }
    }

    private IQueryable<ApplicationUser> ApplyVisibleUserScope(IdentityDbContextApp unitOfWorkDbContext, IQueryable<ApplicationUser> query, Guid tenantId)
    {
        if (_managementGuard.IsCurrentUserHostAdmin())
        {
            return query;
        }

        var elevatedRoleNames = new[]
        {
            TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
        };

        var elevatedUserIds = from userRole in unitOfWorkDbContext.UserRoles
                              join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
                              where role.TenantId == tenantId && elevatedRoleNames.Contains(role.NormalizedName!)
                              select userRole.UserId;

        return query.Where(x => !elevatedUserIds.Contains(x.Id));
    }

    private async Task WriteSecurityLogAsync(
        string action,
        ApplicationUser user,
        string? reasonCode = null,
        string? note = null,
        string? correlationId = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = user.TenantId,
            ApplicationName = "Identity",
            Identity = "UserManagement",
            Action = action,
            UserId = user.Id,
            UserName = user.UserName,
            CreationTime = DateTime.UtcNow,
            ExtraProperties = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["actorUserId"] = _currentUser.Id?.ToString("D"),
                ["actorUserName"] = _currentUser.UserName,
                ["actorRoles"] = string.Join(",", TenantWrightIdentityRoleNames.NormalizeMany(_currentUser.Roles)),
                ["reasonCode"] = reasonCode,
                ["note"] = note?.Trim(),
                ["correlationId"] = correlationId
            })
        });
    }
}
