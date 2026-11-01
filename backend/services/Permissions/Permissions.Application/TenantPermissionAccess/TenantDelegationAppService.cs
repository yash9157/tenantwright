using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using DelegationContracts = Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Definitions;
using Permissions.Application.PermissionManagement;
using Permissions.Application.Seeding;
using Permissions.Domain.PermissionManagement;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.CurrentUser;
using Shared.Data.Exceptions;
using Shared.Uow.Attributes;
using Shared;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Domain.Shared.ApplicationUsers;

namespace Permissions.Application.TenantPermissionAccess;

[ExposeServices(typeof(ITenantDelegationAppService))]
public sealed class TenantDelegationAppService(
    IRequestAuthorizationService authorization,
    ICurrentTenant currentTenant,
    ICurrentUser currentUser,
    IDbContextProvider<PermissionsDbContextApp> dbContextProvider,
    IRepository<PermissionAssignmentOperation, Guid> operations,
    IIdentityPermissionTargetValidator identityTargets,
    PermissionDefinitionManager definitionManager,
    PermissionDefinitionIndex definitions,
    PermissionAssignmentAppService assignment) : ITenantDelegationAppService, ITransientDependency
{
    private const string HostCeilingProviderName = "TenantDelegationCeiling";

    public async Task<IReadOnlyList<TenantDelegationEntryDto>> GetAsync(
        TenantDelegationListInput input,
        CancellationToken cancellationToken = default)
    {
        await authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadTenantAccess, cancellationToken: cancellationToken);
        var tenantId = currentTenant.Id ?? throw new UnauthorizedAccessException("Tenant context is required.");
        return await GetForTenantAsync(tenantId, input, cancellationToken);
    }

    private async Task<IReadOnlyList<TenantDelegationEntryDto>> GetForTenantAsync(
        Guid tenantId,
        TenantDelegationListInput input,
        CancellationToken cancellationToken)
    {
        input ??= new TenantDelegationListInput();
        var db = await dbContextProvider.GetDbContextAsync();
        var query = await db.PermissionGrants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.ProviderName == PermissionProviderNames.User || x.ProviderName == PermissionProviderNames.Role))
            .ToListAsync(cancellationToken);
        query = query.Where(x => Guid.TryParse(x.ProviderKey, out _)).ToList();
        var permissionName = input.PermissionName?.Trim();
        if (!string.IsNullOrWhiteSpace(permissionName)) query = query.Where(x => x.Name == permissionName).ToList();
        if (input.TargetKind is { } targetKind) query = query.Where(x => x.ProviderName == ProviderName(targetKind)).ToList();
        if (input.TargetId is { } targetId) query = query.Where(x => x.ProviderKey == targetId.ToString("D")).ToList();
        var providerKeys = query.Select(x => new { x.ProviderName, x.ProviderKey }).Distinct().ToArray();
        var providerNames = providerKeys.Select(x => x.ProviderName).Distinct(StringComparer.Ordinal).ToArray();
        var providerKeyValues = providerKeys.Select(x => x.ProviderKey).Distinct(StringComparer.Ordinal).ToArray();
        var states = providerKeys.Length == 0
            ? []
            : await db.PermissionAssignmentStates.AsNoTracking()
                .Where(x => x.TenantId == tenantId &&
                            providerNames.Contains(x.ProviderName) &&
                            providerKeyValues.Contains(x.ProviderKey))
                .ToListAsync(cancellationToken);
        var versions = states.ToDictionary(x => (x.ProviderName, x.ProviderKey), x => x.Version.ToString("D"));
        return query.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.ProviderName).ThenBy(x => x.ProviderKey)
            .Select(x => new TenantDelegationEntryDto
            {
                TargetId = Guid.Parse(x.ProviderKey),
                TargetKind = x.ProviderName == PermissionProviderNames.User ? TenantDelegationTargetKind.User : TenantDelegationTargetKind.Role,
                PermissionName = x.Name,
                Effect = x.Effect,
                ExpiresAtUtc = x.ExpiresAtUtc,
                GrantedByUserId = x.GrantedByUserId,
                Reason = x.Reason,
                ProviderVersion = versions.GetValueOrDefault((x.ProviderName, x.ProviderKey), PermissionAssignmentAppService.InitialProviderVersion)
            }).ToArray();
    }

    public async Task<TenantDelegationMutationResult> MutateAsync(
        TenantDelegationMutationInput input,
        CancellationToken cancellationToken = default)
    {
        await authorization.EnsureAuthorizedAsync(PermissionsPolicies.ManageTenantAccess, cancellationToken: cancellationToken);
        var tenantId = currentTenant.Id ?? throw new UnauthorizedAccessException("Tenant context is required.");
        return await MutateForTenantAsync(tenantId, input, cancellationToken);
    }

    public async Task<HostTenantDelegationCeilingDto> GetHostCeilingAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureHostAuthorizedAsync(tenantId, cancellationToken);
        var db = await dbContextProvider.GetDbContextAsync();
        var roleKey = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");
        var eligible = GetHostCeilingPermissionNames();
        var grants = await db.PermissionGrants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == roleKey && eligible.Contains(x.Name))
            .ToListAsync(cancellationToken);
        var grantMap = grants.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var groups = definitionManager.GetGroups()
            .Select(group => new PermissionGroupDto
            {
                Name = group.Name,
                DisplayName = group.DisplayName ?? group.Name,
                Permissions = Flatten(group.Permissions)
                    .Where(permission => eligible.Contains(permission.Name))
                    .Select(permission =>
                    {
                        grantMap.TryGetValue(permission.Name, out var grant);
                        return new PermissionGrantInfoDto
                        {
                            Name = permission.Name,
                            DisplayName = permission.DisplayName,
                            ParentName = permission.ParentName,
                            IsGranted = grant is not null,
                            Effect = grant?.Effect,
                            AllowedProviders = [PermissionProviderNames.Role]
                        };
                    })
                    .ToList()
            })
            .Where(group => group.Permissions.Count > 0)
            .ToList();
        var providerVersion = await db.PermissionAssignmentStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProviderName == PermissionProviderNames.Role && x.ProviderKey == roleKey)
            .Select(x => (Guid?)x.Version)
            .SingleOrDefaultAsync(cancellationToken);

        return new HostTenantDelegationCeilingDto
        {
            TenantId = tenantId,
            TargetDisplayName = "TenantAdmin delegation ceiling",
            ProviderVersion = providerVersion?.ToString("D") ?? PermissionAssignmentAppService.InitialProviderVersion,
            Groups = groups
        };
    }

    [UnitOfWork(true)]
    public async Task<HostTenantDelegationCeilingUpdateResult> UpdateHostCeilingAsync(
        Guid tenantId,
        string idempotencyKey,
        UpdateHostTenantDelegationCeilingInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsureHostAuthorizedAsync(tenantId, cancellationToken);
        if (input is null || input.Changes.Count == 0 || string.IsNullOrWhiteSpace(input.ExpectedProviderVersion) ||
            string.IsNullOrWhiteSpace(input.Reason) ||
            string.IsNullOrWhiteSpace(input.TicketId) || string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Trim().Length > 128)
            throw new ArgumentException("Host tenant delegation requires changes, expected version, idempotency key, reason, and ticket reference.", nameof(input));

        var expectedProviderVersion = input.ExpectedProviderVersion.Trim();
        var changes = input.Changes
            .Where(change => !string.IsNullOrWhiteSpace(change.PermissionName))
            .GroupBy(change => change.PermissionName.Trim(), StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(change => change.PermissionName, StringComparer.Ordinal)
            .ToArray();
        if (changes.Length != input.Changes.Count)
            throw new ArgumentException("Permission changes must contain unique, non-empty names.", nameof(input));

        var db = await dbContextProvider.GetDbContextAsync();
        var eligible = GetHostCeilingPermissionNames();
        var invalid = changes.Select(change => change.PermissionName).Where(name => !eligible.Contains(name)).ToArray();
        if (invalid.Length > 0)
            throw new UnauthorizedAccessException($"Permissions are not tenant-delegable: {string.Join(", ", invalid)}");

        var actorId = currentUser.Id ?? throw new UnauthorizedAccessException("A host operator subject is required.");
        var operationKey = idempotencyKey.Trim();
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            TenantId = tenantId,
            ExpectedProviderVersion = expectedProviderVersion,
            Changes = changes.Select(change => new { change.PermissionName, change.IsGranted, Effect = (int)change.Effect }),
            Reason = input.Reason.Trim(),
            TicketId = input.TicketId.Trim()
        }))));
        var operationProviderKey = tenantId.ToString("D");
        var existingOperation = await db.PermissionAssignmentOperations.SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.ActorUserId == actorId && x.ProviderName == HostCeilingProviderName &&
            x.ProviderKey == operationProviderKey && x.IdempotencyKey == operationKey, cancellationToken);
        if (existingOperation is not null)
        {
            if (!string.Equals(existingOperation.RequestHash, requestHash, StringComparison.Ordinal))
                throw new SharedConflictException("delegation_ceiling_idempotency_conflict", "The idempotency key was already used for a different ceiling update.");
            if (existingOperation.ResultPayload is not null)
                return JsonSerializer.Deserialize<HostTenantDelegationCeilingUpdateResult>(existingOperation.ResultPayload)!;
            throw new SharedConflictException("delegation_ceiling_operation_in_progress", "An equivalent ceiling update is already being processed.");
        }

        var operation = new PermissionAssignmentOperation(Guid.CreateVersion7(), tenantId, actorId,
            HostCeilingProviderName, operationProviderKey, operationKey, requestHash, DateTime.UtcNow);
        db.PermissionAssignmentOperations.Add(operation);
        var roleKey = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");
        var existingGrants = await db.PermissionGrants
            .Where(x => x.TenantId == tenantId && x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == roleKey && changes.Select(change => change.PermissionName).Contains(x.Name))
            .ToListAsync(cancellationToken);
        var existingByName = existingGrants.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var removedNames = changes
            .Where(change => !change.IsGranted || change.Effect != PermissionGrantEffect.Allow)
            .Select(change => change.PermissionName)
            .ToHashSet(StringComparer.Ordinal);

        var revokedDownstream = await RevokeDownstreamGrantsAsync(
            db, tenantId, roleKey, removedNames, input.Reason.Trim(), input.TicketId.Trim(), cancellationToken);
        await assignment.UpdateTenantProviderPermissionsAsync(
            tenantId,
            PermissionProviderNames.Role,
            roleKey,
            new UpdatePermissionsDto
            {
                ExpectedProviderVersion = expectedProviderVersion,
                Permissions = changes.Select(change => new UpdatePermissionDto
                {
                    Name = change.PermissionName,
                    IsGranted = change.IsGranted,
                    Effect = change.Effect,
                    Reason = input.Reason.Trim(),
                    TicketId = input.TicketId.Trim()
                }).ToList()
            },
            cancellationToken);

        var refreshedDb = await dbContextProvider.GetDbContextAsync();
        var currentVersion = await refreshedDb.PermissionAssignmentStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProviderName == PermissionProviderNames.Role && x.ProviderKey == roleKey)
            .Select(x => (Guid?)x.Version)
            .SingleOrDefaultAsync(cancellationToken);
        var pendingNames = await refreshedDb.PermissionGrantChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == roleKey && x.Status == PermissionGrantChangeRequestStatus.Pending &&
                        changes.Select(change => change.PermissionName).Contains(x.PermissionName))
            .Select(x => x.PermissionName)
            .Distinct()
            .ToListAsync(cancellationToken);
        var applied = changes.Where(change => !pendingNames.Contains(change.PermissionName, StringComparer.Ordinal) &&
            (!existingByName.TryGetValue(change.PermissionName, out var before) ||
             before.Effect != change.Effect || !change.IsGranted)).Select(change => change.PermissionName).ToList();
        var result = new HostTenantDelegationCeilingUpdateResult
        {
            ProviderVersion = currentVersion?.ToString("D") ?? PermissionAssignmentAppService.InitialProviderVersion,
            AppliedPermissionNames = applied,
            PendingApprovalPermissionNames = pendingNames,
            UnchangedPermissionNames = changes.Select(change => change.PermissionName)
                .Except(applied, StringComparer.Ordinal).Except(pendingNames, StringComparer.Ordinal).ToList(),
            RevokedDownstreamGrantCount = revokedDownstream
        };
        operation.Complete(JsonSerializer.Serialize(result), DateTime.UtcNow);
        await refreshedDb.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<TenantDelegationMutationResult> MutateForTenantAsync(
        Guid tenantId,
        TenantDelegationMutationInput input,
        CancellationToken cancellationToken)
    {
        if (input.TargetId == Guid.Empty || string.IsNullOrWhiteSpace(input.PermissionName))
            throw new ArgumentException("A target and permission are required.");
        if (currentUser.Id is not { } actorId || actorId == Guid.Empty || string.IsNullOrWhiteSpace(input.IdempotencyKey))
            throw new ArgumentException("An authenticated actor and idempotency key are required.");

        var permissionName = input.PermissionName.Trim();
        if (!definitions.TryGet(permissionName, out var permission) || input.IsGranted && !permission.IsEnabled)
            throw new ArgumentException(input.IsGranted ? "The permission is unknown or disabled." : "The permission is unknown.", nameof(input));

        var db = await dbContextProvider.GetDbContextAsync();
        var delegation = permission.Delegation;
        if (input.IsGranted && !GetTenantDelegablePermissionNames().Contains(permissionName))
            throw new UnauthorizedAccessException("This permission is not tenant-delegable.");
        if (input.IsGranted && !await TenantAdminCeilingAllowsAsync(db, tenantId, permissionName, cancellationToken))
            throw new UnauthorizedAccessException("This permission is outside the tenant administrator's delegation ceiling.");

        var providerName = input.IsGranted ? input.TargetKind switch
        {
            TenantDelegationTargetKind.User when delegation is null ||
                (delegation.AllowedTargetKinds & DelegationContracts.PermissionDelegationTargetKinds.User) != 0 => PermissionProviderNames.User,
            TenantDelegationTargetKind.Role when delegation is null ||
                (delegation.AllowedTargetKinds & DelegationContracts.PermissionDelegationTargetKinds.Role) != 0 => PermissionProviderNames.Role,
            _ => throw new UnauthorizedAccessException("The target kind is not allowed by the published delegation policy.")
        } : ProviderName(input.TargetKind);
        if (input.IsGranted &&
            delegation is not null &&
            (input.Effect == PermissionGrantEffect.Allow && (delegation.AllowedEffects & DelegationContracts.PermissionDelegationEffects.Allow) == 0 ||
             input.Effect == PermissionGrantEffect.Deny && (delegation.AllowedEffects & DelegationContracts.PermissionDelegationEffects.Deny) == 0))
            throw new UnauthorizedAccessException("The requested effect is not allowed by the published delegation policy.");

        if (input.TargetKind == TenantDelegationTargetKind.User)
        {
            var user = await identityTargets.GetUserAsync(input.TargetId, tenantId, cancellationToken);
            if (input.IsGranted && (!user.IsActive || user.IsLockedOut))
                throw new UnauthorizedAccessException("Inactive or locked users cannot receive tenant delegation.");
        }
        else
        {
            var role = await identityTargets.GetRoleAsync(input.TargetId, tenantId, cancellationToken);
            if (role.IsStatic) throw new UnauthorizedAccessException("Static roles cannot receive tenant delegation.");
            if (input.IsGranted && !role.IsActive)
                throw new UnauthorizedAccessException("Non-active roles cannot receive tenant delegation.");
        }

        if (input.IsGranted && delegation?.MaximumExpiry is { } maximum && input.ExpiresAtUtc is { } expiry &&
            expiry > DateTime.UtcNow.Add(maximum))
            throw new ArgumentException("The requested expiry exceeds the published delegation policy.", nameof(input));
        if (input.IsGranted && input.ExpiresAtUtc is { } suppliedExpiry && suppliedExpiry <= DateTime.UtcNow)
            throw new ArgumentException("The expiry must be in the future.", nameof(input));

        var providerKey = input.TargetId.ToString("D");
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        var operationQuery = await operations.GetQueryableAsync();
        var existingOperation = await operationQuery.SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.ActorUserId == actorId && x.ProviderName == providerName &&
            x.ProviderKey == providerKey && x.IdempotencyKey == input.IdempotencyKey.Trim(), cancellationToken);
        if (existingOperation is not null)
        {
            if (!string.Equals(existingOperation.RequestHash, requestHash, StringComparison.Ordinal))
                throw new SharedConflictException("delegation_idempotency_conflict", "The idempotency key was already used for a different delegation request.");
            if (existingOperation.ResultPayload is not null)
                return JsonSerializer.Deserialize<TenantDelegationMutationResult>(existingOperation.ResultPayload)!;
            throw new SharedConflictException("delegation_operation_in_progress", "An equivalent delegation request is already being processed.");
        }
        var operation = new PermissionAssignmentOperation(Guid.CreateVersion7(), tenantId, actorId, providerName,
            providerKey, input.IdempotencyKey.Trim(), requestHash, DateTime.UtcNow);
        await operations.InsertAsync(operation, autoSave: false, cancellationToken);
        await assignment.UpdateTenantProviderPermissionsAsync(tenantId, providerName, providerKey,
            new UpdatePermissionsDto
            {
                ExpectedProviderVersion = input.ExpectedProviderVersion,
                Permissions =
                [new UpdatePermissionDto
                {
                    Name = permissionName,
                    IsGranted = input.IsGranted,
                    Effect = input.Effect,
                    ExpiresAtUtc = input.ExpiresAtUtc,
                    Reason = input.Reason,
                    TicketId = input.TicketId
                }]
            }, cancellationToken);

        var dbAfter = await dbContextProvider.GetDbContextAsync();
        var currentProviderVersion = await dbAfter.PermissionAssignmentStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProviderName == providerName && x.ProviderKey == providerKey)
            .Select(x => (Guid?)x.Version)
            .SingleOrDefaultAsync(cancellationToken);
        var result = new TenantDelegationMutationResult
        {
            TargetId = input.TargetId,
            TargetKind = input.TargetKind,
            PermissionName = permissionName,
            IsGranted = input.IsGranted,
            Effect = input.Effect,
            PendingApproval = input.IsGranted && permission.RequiresApproval && input.Effect == PermissionGrantEffect.Allow,
            CurrentProviderVersion = currentProviderVersion?.ToString("D") ?? PermissionAssignmentAppService.InitialProviderVersion
        };
        operation.Complete(JsonSerializer.Serialize(result), DateTime.UtcNow);
        await dbAfter.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<TenantDelegationOptionDto>> GetOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        await authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadTenantAccess, cancellationToken: cancellationToken);
        var tenantId = currentTenant.Id ?? throw new UnauthorizedAccessException("Tenant context is required.");
        return await GetOptionsCoreAsync(tenantId, cancellationToken);
    }

    private async Task<IReadOnlyList<TenantDelegationOptionDto>> GetOptionsCoreAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var db = await dbContextProvider.GetDbContextAsync();
        var roleKey = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");
        var now = DateTime.UtcNow;
        var ceiling = await db.PermissionGrants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == roleKey && x.Effect == PermissionGrantEffect.Allow &&
                        (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc > now))
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);
        var ceilingNames = ceiling.ToHashSet(StringComparer.Ordinal);
        var eligible = GetTenantDelegablePermissionNames();
        return eligible
            .Where(ceilingNames.Contains)
            .Select(permissionName =>
            {
                definitions.TryGet(permissionName, out var definition);
                var delegation = definition!.Delegation;
                var separator = permissionName.IndexOf('.', StringComparison.Ordinal);
                return new TenantDelegationOptionDto
                {
                    PermissionName = permissionName,
                    DisplayName = definition.DisplayName ?? permissionName,
                    GroupName = separator > 0 ? permissionName[..separator] : permissionName,
                    AllowsUser = delegation is null ||
                        (delegation.AllowedTargetKinds & DelegationContracts.PermissionDelegationTargetKinds.User) != 0,
                    AllowsRole = delegation is null ||
                        (delegation.AllowedTargetKinds & DelegationContracts.PermissionDelegationTargetKinds.Role) != 0,
                    AllowsAllow = delegation is null ||
                        (delegation.AllowedEffects & DelegationContracts.PermissionDelegationEffects.Allow) != 0,
                    AllowsDeny = delegation is null ||
                        (delegation.AllowedEffects & DelegationContracts.PermissionDelegationEffects.Deny) != 0,
                    RequiresApproval = definition.RequiresApproval,
                    MaximumExpirySeconds = delegation?.MaximumExpiry is { } expiry
                        ? checked((int)expiry.TotalSeconds)
                        : null
                };
            })
            .OrderBy(option => option.GroupName, StringComparer.Ordinal)
            .ThenBy(option => option.DisplayName, StringComparer.Ordinal)
            .ThenBy(option => option.PermissionName, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task EnsureHostAuthorizedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || currentTenant.Id.HasValue || !currentUser.IsAuthenticated ||
            (!currentUser.IsInRole("admin") && !currentUser.IsInRole("HostAdmin")))
            throw new UnauthorizedAccessException("Host tenant delegation requires a host administrator and an explicit tenant.");

        await authorization.EnsureAuthorizedAsync(PermissionsPolicies.ManagePermissionGrants, cancellationToken: cancellationToken);
    }

    private static IEnumerable<PermissionDefinition> Flatten(IEnumerable<PermissionDefinition> permissions)
    {
        foreach (var permission in permissions)
        {
            yield return permission;
            foreach (var child in Flatten(permission.Children)) yield return child;
        }
    }

    private HashSet<string> GetHostCeilingPermissionNames() =>
        TenantAdminPermissionDefaults.ManagementPermissionRoots
            .Where(name => !name.StartsWith("Permissions.", StringComparison.Ordinal) &&
                           definitions.TryGet(name, out var definition) && definition.IsEnabled)
            .ToHashSet(StringComparer.Ordinal);

    private HashSet<string> GetTenantDelegablePermissionNames() =>
        GetHostCeilingPermissionNames()
            .Where(name => definitions.TryGet(name, out var definition) &&
                           (definition.Delegation is null ||
                            (definition.Delegation.AllowedScopeKinds &
                             DelegationContracts.PermissionDelegationScopeKinds.Tenant) != 0))
            .ToHashSet(StringComparer.Ordinal);

    private static async Task<bool> TenantAdminCeilingAllowsAsync(
        PermissionsDbContextApp db,
        Guid tenantId,
        string permissionName,
        CancellationToken cancellationToken)
    {
        var roleKey = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");
        var now = DateTime.UtcNow;
        return await db.PermissionGrants.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenantId && x.ProviderName == PermissionProviderNames.Role && x.ProviderKey == roleKey &&
            x.Name == permissionName && x.Effect == PermissionGrantEffect.Allow &&
            (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc > now), cancellationToken);
    }

    private async Task<int> RevokeDownstreamGrantsAsync(
        PermissionsDbContextApp db,
        Guid tenantId,
        string tenantAdminRoleKey,
        HashSet<string> permissionNames,
        string reason,
        string ticketId,
        CancellationToken cancellationToken)
    {
        if (permissionNames.Count == 0) return 0;
        var grants = await db.PermissionGrants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && permissionNames.Contains(x.Name) &&
                        (x.ProviderName == PermissionProviderNames.User || x.ProviderName == PermissionProviderNames.Role) &&
                        !(x.ProviderName == PermissionProviderNames.Role && x.ProviderKey == tenantAdminRoleKey))
            .ToListAsync(cancellationToken);
        var providers = grants.GroupBy(x => (x.ProviderName, x.ProviderKey)).ToArray();
        if (providers.Length == 0) return 0;
        var providerNames = providers.Select(group => group.Key.ProviderName).Distinct().ToArray();
        var providerKeys = providers.Select(group => group.Key.ProviderKey).Distinct().ToArray();
        var versions = await db.PermissionAssignmentStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && providerNames.Contains(x.ProviderName) && providerKeys.Contains(x.ProviderKey))
            .ToDictionaryAsync(x => (x.ProviderName, x.ProviderKey), x => x.Version.ToString("D"), cancellationToken);
        foreach (var provider in providers)
        {
            await assignment.UpdateTenantProviderPermissionsAsync(
                tenantId,
                provider.Key.ProviderName,
                provider.Key.ProviderKey,
                new UpdatePermissionsDto
                {
                    ExpectedProviderVersion = versions.GetValueOrDefault(
                        provider.Key,
                        PermissionAssignmentAppService.InitialProviderVersion),
                    Permissions = provider.Select(grant => new UpdatePermissionDto
                    {
                        Name = grant.Name,
                        IsGranted = false,
                        Reason = reason,
                        TicketId = ticketId
                    }).ToList()
                },
                cancellationToken);
        }
        return grants.Count;
    }

    private static string ProviderName(TenantDelegationTargetKind targetKind) => targetKind switch
    {
        TenantDelegationTargetKind.User => PermissionProviderNames.User,
        TenantDelegationTargetKind.Role => PermissionProviderNames.Role,
        _ => throw new ArgumentOutOfRangeException(nameof(targetKind))
    };
}
