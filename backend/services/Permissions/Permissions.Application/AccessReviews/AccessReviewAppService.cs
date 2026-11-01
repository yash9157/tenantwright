using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.AccessReviews;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Definitions;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;
using Permissions.Domain.PermissionManagement;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Security;

namespace Permissions.Application.AccessReviews;

public sealed class AccessReviewAppService : IAccessReviewAppService, ITransientDependency
{
    private static readonly TimeSpan MaxMatrixCacheExpiration = TimeSpan.FromMinutes(10);

    private readonly IDbContextProvider<PermissionsDbContextApp> _dbContextProvider;
    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IDataFilter _dataFilter;
    private readonly PermissionAssignmentAppService _assignmentService;
    private readonly PermissionDefinitionManager _definitions;
    private readonly IIdentityPermissionTargetValidator _identityTargets;
    private readonly RolePrivilegeMatrixCache _matrixCache;
    private readonly TenantDatabaseFanOut _tenantDatabases;

    public AccessReviewAppService(
        IDbContextProvider<PermissionsDbContextApp> dbContextProvider,
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        IDataFilter dataFilter,
        PermissionAssignmentAppService assignmentService,
        PermissionDefinitionManager definitions,
        IIdentityPermissionTargetValidator identityTargets,
        RolePrivilegeMatrixCache matrixCache,
        TenantDatabaseFanOut tenantDatabases)
    {
        _dbContextProvider = dbContextProvider;
        _authorization = authorization;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _dataFilter = dataFilter;
        _assignmentService = assignmentService;
        _definitions = definitions;
        _identityTargets = identityTargets;
        _matrixCache = matrixCache;
        _tenantDatabases = tenantDatabases;
    }

    public async Task<AccessReviewCampaignDto> CreateCampaignAsync(
        CreateAccessReviewCampaignInput input,
        CancellationToken cancellationToken = default)
    {
        input ??= new CreateAccessReviewCampaignInput();
        var tenantId = await AuthorizeAndResolveTenantAsync(input.TenantId, cancellationToken);
        return await ExecuteInTenantScopeAsync(tenantId, async () =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var now = DateTime.UtcNow;
            var creatorUserId = RequireCurrentUserId();
            var dueAtUtc = NormalizeUtc(input.DueAtUtc);
            if (dueAtUtc <= now)
            {
                throw new SharedValidationException("The campaign due date must be in the future.");
            }

            var activeCampaignExists = await dbContext.AccessReviewCampaigns
                .AnyAsync(x => x.TenantId == tenantId && x.Status == AccessReviewCampaignStatus.Active, cancellationToken);
            if (activeCampaignExists)
            {
                throw new SharedValidationException("Close the active access review campaign before opening a new one.");
            }

            var campaign = new AccessReviewCampaign(
                Guid.CreateVersion7(),
                tenantId,
                input.Name?.Trim() ?? string.Empty,
                now,
                dueAtUtc,
                creatorUserId);
            dbContext.AccessReviewCampaigns.Add(campaign);

            var grants = await dbContext.PermissionGrants
                .Where(x => x.TenantId == tenantId &&
                            x.Effect == PermissionGrantEffect.Allow &&
                            (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc > now))
                .OrderBy(x => x.ProviderName)
                .ThenBy(x => x.ProviderKey)
                .ThenBy(x => x.Name)
                .ToListAsync(cancellationToken);
            foreach (var grant in grants)
            {
                dbContext.AccessReviewItems.Add(new AccessReviewItem(
                    Guid.CreateVersion7(),
                    tenantId,
                    campaign.Id,
                    grant.Id,
                    grant.ProviderName,
                    grant.ProviderKey,
                    grant.Name,
                    grant.Effect,
                    grant.CreationTimeUtc,
                    grant.ExpiresAtUtc,
                    grant.Reason,
                    grant.TicketId,
                    grant.GrantedByUserId));
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            return new AccessReviewCampaignDto
            {
                Id = campaign.Id,
                TenantId = tenantId,
                Name = campaign.Name,
                SnapshotAtUtc = campaign.SnapshotAtUtc,
                DueAtUtc = campaign.DueAtUtc,
                Status = campaign.Status
            };
        });
    }

    public async Task<PagedAccessReviewCampaignResultDto> GetCampaignsAsync(
        GetAccessReviewCampaignsInput input,
        CancellationToken cancellationToken = default)
    {
        input ??= new GetAccessReviewCampaignsInput();
        var tenantId = await AuthorizeAndResolveTenantAsync(input.TenantId, cancellationToken);
        return await ExecuteInTenantScopeAsync(tenantId, async () =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var skip = Math.Max(0, input.SkipCount);
            var take = Math.Clamp(input.MaxResultCount, 1, 200);
            var query = dbContext.AccessReviewCampaigns.Where(x => x.TenantId == tenantId);
            if (input.Status.HasValue)
            {
                query = query.Where(x => x.Status == input.Status.Value);
            }

            var totalCount = await query.LongCountAsync(cancellationToken);
            var campaigns = await query
                .OrderByDescending(x => x.SnapshotAtUtc)
                .Skip(skip)
                .Take(take)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var campaignIds = campaigns.Select(x => x.Id).ToArray();
            var counts = await dbContext.AccessReviewItems
                .Where(x => x.TenantId == tenantId && campaignIds.Contains(x.CampaignId))
                .GroupBy(x => new { x.CampaignId, x.Status })
                .Select(x => new CampaignStatusCount(x.Key.CampaignId, x.Key.Status, x.LongCount()))
                .ToListAsync(cancellationToken);

            return new PagedAccessReviewCampaignResultDto
            {
                TotalCount = totalCount,
                Items = campaigns.Select(campaign => MapCampaign(campaign, counts.Where(x => x.CampaignId == campaign.Id))).ToList()
            };
        });
    }

    public async Task<PagedAccessReviewItemResultDto> GetItemsAsync(
        Guid campaignId,
        GetAccessReviewItemsInput input,
        CancellationToken cancellationToken = default)
    {
        input ??= new GetAccessReviewItemsInput();
        var tenantId = await AuthorizeAndResolveTenantAsync(input.TenantId, cancellationToken);
        return await ExecuteInTenantScopeAsync(tenantId, async () =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            await EnsureCampaignAsync(campaignId, tenantId, cancellationToken);
            var skip = Math.Max(0, input.SkipCount);
            var take = Math.Clamp(input.MaxResultCount, 1, 200);
            var query = dbContext.AccessReviewItems.Where(x => x.TenantId == tenantId && x.CampaignId == campaignId);
            if (input.Status.HasValue)
            {
                query = query.Where(x => x.Status == input.Status.Value);
            }

            var totalCount = await query.LongCountAsync(cancellationToken);
            var items = await query
                .OrderBy(x => x.Status)
                .ThenBy(x => x.ProviderName)
                .ThenBy(x => x.ProviderKey)
                .ThenBy(x => x.PermissionName)
                .Skip(skip)
                .Take(take)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var sourceGrants = await LoadSourceGrantsAsync(tenantId, items.Select(x => x.SourceGrantId), cancellationToken);
            var displayNames = await ResolveDisplayNamesAsync(tenantId, items, cancellationToken);
            var groups = BuildPermissionGroups();

            return new PagedAccessReviewItemResultDto
            {
                TotalCount = totalCount,
                Items = items.Select(item => MapItem(
                    item,
                    sourceGrants.GetValueOrDefault(item.SourceGrantId),
                    displayNames.GetValueOrDefault((item.ProviderName, item.ProviderKey), item.ProviderKey),
                    groups.GetValueOrDefault(item.PermissionName, "Other"))).ToList()
            };
        });
    }

    public async Task<AccessReviewItemDto> DecideItemAsync(
        Guid campaignId,
        Guid itemId,
        DecideAccessReviewItemInput input,
        CancellationToken cancellationToken = default)
    {
        input ??= new DecideAccessReviewItemInput();
        var tenantId = await AuthorizeAndResolveTenantAsync(input.TenantId, cancellationToken);
        return await ExecuteInTenantScopeAsync(tenantId, async () =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var reviewerUserId = RequireCurrentUserId();
            var campaign = await EnsureCampaignAsync(campaignId, tenantId, cancellationToken);
            if (campaign.Status != AccessReviewCampaignStatus.Active)
            {
                throw new SharedValidationException("Closed access review campaigns cannot receive decisions.");
            }

            var item = await dbContext.AccessReviewItems.SingleOrDefaultAsync(
                x => x.Id == itemId && x.CampaignId == campaignId && x.TenantId == tenantId,
                cancellationToken) ?? throw new EntityNotFoundException(typeof(AccessReviewItem), itemId);
            if (item.Version != input.ExpectedVersion || input.ExpectedVersion <= 0)
            {
                throw Conflict(AccessReviewConflictCode.VersionConflict, item);
            }

            EnsureNotSelfReview(item, reviewerUserId);
            var decisionReason = Check.NotNullOrWhiteSpace(
                input.DecisionReason?.Trim(),
                nameof(input.DecisionReason),
                AccessReviewConsts.MaxDecisionReasonLength);
            var sourceGrant = await dbContext.PermissionGrants.SingleOrDefaultAsync(
                x => x.Id == item.SourceGrantId && x.TenantId == tenantId,
                cancellationToken);
            if (!MatchesSnapshot(sourceGrant, item))
            {
                throw new SharedValidationException("This grant changed after the review snapshot. Refresh the campaign evidence before deciding.");
            }

            switch (input.Decision)
            {
                case AccessReviewDecision.Certify:
                    item.Certify(reviewerUserId, decisionReason, DateTime.UtcNow);
                    break;
                case AccessReviewDecision.Revoke:
                    await _assignmentService.RevokeForAccessReviewAsync(
                        tenantId,
                        item.ProviderName,
                        item.ProviderKey,
                        item.PermissionName,
                        decisionReason,
                        item.TicketId,
                        cancellationToken);
                    item.Revoke(reviewerUserId, decisionReason, DateTime.UtcNow);
                    break;
                default:
                    throw new SharedValidationException("The access review decision is invalid.");
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw Conflict(AccessReviewConflictCode.ConcurrentDecision);
            }
            var displayNames = await ResolveDisplayNamesAsync(tenantId, [item], cancellationToken);
            return MapItem(
                item,
                input.Decision == AccessReviewDecision.Revoke ? null : sourceGrant,
                displayNames.GetValueOrDefault((item.ProviderName, item.ProviderKey), item.ProviderKey),
                BuildPermissionGroups().GetValueOrDefault(item.PermissionName, "Other"));
        });
    }

    public async Task<AccessReviewCampaignDto> CloseCampaignAsync(
        Guid campaignId,
        CloseAccessReviewCampaignInput input,
        CancellationToken cancellationToken = default)
    {
        input ??= new CloseAccessReviewCampaignInput();
        var tenantId = await AuthorizeAndResolveTenantAsync(input.TenantId, cancellationToken);
        return await ExecuteInTenantScopeAsync(tenantId, async () =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var reviewerUserId = RequireCurrentUserId();
            var campaign = await EnsureCampaignAsync(campaignId, tenantId, cancellationToken);
            var now = DateTime.UtcNow;
            var pendingItems = await dbContext.AccessReviewItems
                .Where(x => x.TenantId == tenantId && x.CampaignId == campaignId && x.Status == AccessReviewItemStatus.Pending)
                .ToListAsync(cancellationToken);
            foreach (var item in pendingItems)
            {
                item.Expire(now);
            }

            campaign.Close(reviewerUserId, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            var counts = await dbContext.AccessReviewItems
                .Where(x => x.TenantId == tenantId && x.CampaignId == campaignId)
                .GroupBy(x => x.Status)
                .Select(x => new { Status = x.Key, Count = x.LongCount() })
                .ToListAsync(cancellationToken);
            return MapCampaign(campaign, counts.Select(x => new CampaignStatusCount(campaignId, x.Status, x.Count)));
        });
    }

    public async Task<RolePrivilegeMatrixDto> GetRolePrivilegeMatrixAsync(
        GetRolePrivilegeMatrixInput input,
        CancellationToken cancellationToken = default)
    {
        input ??= new GetRolePrivilegeMatrixInput();
        var tenantId = await AuthorizeAndResolveTenantAsync(input.TenantId, cancellationToken);
        var maxRoles = Math.Clamp(input.MaxRoles, 3, 200);
        return await ExecuteInTenantScopeAsync(tenantId, async () =>
        {
            var cacheKey = PermissionCacheKeys.BuildRolePrivilegeMatrixKey(tenantId);
            TimeSpan? cappedExpiration = null;
            HybridCacheEntryOptions OptionsFactory() => new()
            {
                Expiration = cappedExpiration ?? MaxMatrixCacheExpiration
            };

            var matrix = await _matrixCache.GetOrCreateAsync(
                cacheKey,
                async () =>
                {
                    var built = await BuildRolePrivilegeMatrixAsync(tenantId, 200, cancellationToken);
                    cappedExpiration = ComputeMatrixCacheExpiration(built.NearestExpiryUtc);
                    return built.Matrix;
                },
                optionsFactory: OptionsFactory,
                considerUow: true,
                token: cancellationToken)
                ?? throw new InvalidOperationException("The role-privilege matrix cache returned no value.");
            return TrimRolePrivilegeMatrix(matrix, maxRoles);
        });
    }

    private async Task<(RolePrivilegeMatrixDto Matrix, DateTime? NearestExpiryUtc)> BuildRolePrivilegeMatrixAsync(
        Guid tenantId,
        int maxRoles,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var grants = await dbContext.PermissionGrants
            .Where(x => x.TenantId == tenantId &&
                        x.ProviderName == PermissionProviderNames.Role &&
                        x.Effect == PermissionGrantEffect.Allow &&
                        (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc > now))
            .AsNoTracking()
            .Select(x => new { x.ProviderKey, x.Name, x.ExpiresAtUtc })
            .ToListAsync(cancellationToken);
        var groupByPermission = BuildPermissionGroups();
        var groupTotals = groupByPermission
            .GroupBy(x => x.Value, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var roleIds = grants
            .Select(x => Guid.TryParse(x.ProviderKey, out var roleId) ? roleId : Guid.Empty)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .OrderBy(x => x)
            .Take(maxRoles)
            .ToArray();
        var targets = await ResolveTargetsAsync(
            roleIds.Select(id => new IdentityPermissionTargetReference(IdentityPermissionTargetKind.Role, id, tenantId)),
            cancellationToken);
        var roleNames = targets
            .Where(x => x.Kind == IdentityPermissionTargetKind.Role && x.TenantId == tenantId)
            .ToDictionary(x => x.Id, x => x.DisplayName, EqualityComparer<Guid>.Default);
        var roles = roleIds
            .Where(roleNames.ContainsKey)
            .Select(roleId => new RolePrivilegeMatrixRoleDto
            {
                RoleId = roleId,
                RoleName = roleNames[roleId],
                Cells = groupTotals.Keys
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .Select(groupName => new RolePrivilegeMatrixCellDto
                    {
                        GroupName = groupName,
                        GrantedCount = grants.Count(x => x.ProviderKey == roleId.ToString("D") && groupByPermission.GetValueOrDefault(x.Name, "Other") == groupName),
                        GroupTotal = groupTotals[groupName]
                    })
                    .ToList()
            })
            .OrderBy(x => x.RoleName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groups = groupTotals
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new RolePrivilegeMatrixGroupDto
            {
                Name = x.Key,
                DisplayName = x.Key,
                PermissionCount = x.Value
            })
            .ToList();

        var nearestExpiryUtc = grants
            .Where(x => x.ExpiresAtUtc.HasValue)
            .Select(x => x.ExpiresAtUtc)
            .OrderBy(x => x)
            .FirstOrDefault();

        return (new RolePrivilegeMatrixDto
        {
            TenantId = tenantId,
            GeneratedAtUtc = DateTime.UtcNow,
            Groups = groups,
            Roles = roles
        }, nearestExpiryUtc);
    }

    /// <summary>
    /// Caps the matrix cache entry at the nearest active role-grant expiry so a time-bound role
    /// permission cannot remain represented after it expires.
    /// </summary>
    public static TimeSpan ComputeMatrixCacheExpiration(DateTime? nearestExpiryUtc)
    {
        if (!nearestExpiryUtc.HasValue)
        {
            return MaxMatrixCacheExpiration;
        }

        var untilExpiry = nearestExpiryUtc.Value - DateTime.UtcNow;
        if (untilExpiry <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(1);
        }

        return untilExpiry < MaxMatrixCacheExpiration ? untilExpiry : MaxMatrixCacheExpiration;
    }

    private static RolePrivilegeMatrixDto TrimRolePrivilegeMatrix(RolePrivilegeMatrixDto matrix, int maxRoles)
    {
        return new RolePrivilegeMatrixDto
        {
            TenantId = matrix.TenantId,
            GeneratedAtUtc = matrix.GeneratedAtUtc,
            Groups = matrix.Groups,
            Roles = matrix.Roles.Take(maxRoles).ToList()
        };
    }

    private async Task<Guid> AuthorizeAndResolveTenantAsync(Guid requestedTenantId, CancellationToken cancellationToken)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ManageAccessReviews, cancellationToken);
        if (requestedTenantId == Guid.Empty)
        {
            throw new SharedValidationException("A tenant is required for access review.");
        }

        if (_currentTenant.Id.HasValue && _currentTenant.Id.Value != requestedTenantId)
        {
            throw new UnauthorizedAccessException("Access review cannot cross the active tenant boundary.");
        }

        if (!_currentTenant.Id.HasValue && _currentUser.TenantId.HasValue)
        {
            throw new UnauthorizedAccessException("A tenant user cannot select another tenant for access review.");
        }

        return requestedTenantId;
    }

    /// <summary>
    /// Runs one tenant's review in the database holding that tenant's grants: tenant callers already are there; a
    /// host reviewer enters the tenant's dedicated database when it has one, otherwise stays on the shared database
    /// with the tenant filter lifted (every query carries an explicit TenantId predicate). Campaign, decision and
    /// revocation writes therefore always land where the tenant's grants live.
    /// </summary>
    private async Task<T> ExecuteInTenantScopeAsync<T>(Guid tenantId, Func<Task<T>> action)
    {
        if (_currentTenant.Id.HasValue)
        {
            return await action();
        }

        using var tenantDatabase = _tenantDatabases.EnterDatabase(await _tenantDatabases.FindTenantDatabaseAsync<PermissionsDbContextApp>(tenantId));
        if (_currentTenant.Id.HasValue)
        {
            return await action();
        }

        using (_dataFilter.Disable<IMultiTenant>())
        {
            return await action();
        }
    }

    private async Task<AccessReviewCampaign> EnsureCampaignAsync(Guid campaignId, Guid tenantId, CancellationToken cancellationToken)
        => await (await _dbContextProvider.GetDbContextAsync()).AccessReviewCampaigns.SingleOrDefaultAsync(
               x => x.Id == campaignId && x.TenantId == tenantId,
               cancellationToken)
           ?? throw new EntityNotFoundException(typeof(AccessReviewCampaign), campaignId);

    private async Task<Dictionary<Guid, PermissionGrant>> LoadSourceGrantsAsync(
        Guid tenantId,
        IEnumerable<Guid> sourceGrantIds,
        CancellationToken cancellationToken)
    {
        var ids = sourceGrantIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return await dbContext.PermissionGrants
            .Where(x => x.TenantId == tenantId && ids.Contains(x.Id))
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id, cancellationToken);
    }

    private async Task<Dictionary<(string ProviderName, string ProviderKey), string>> ResolveDisplayNamesAsync(
        Guid tenantId,
        IReadOnlyCollection<AccessReviewItem> items,
        CancellationToken cancellationToken)
    {
        var references = items
            .Select(item => TryCreateTargetReference(item.ProviderName, item.ProviderKey, tenantId))
            .Where(reference => reference != null)
            .Select(reference => reference!)
            .Distinct()
            .ToArray();
        var targets = await ResolveTargetsAsync(references, cancellationToken);
        return targets.ToDictionary(
            target => (target.Kind == IdentityPermissionTargetKind.Role ? PermissionProviderNames.Role : PermissionProviderNames.User, target.Id.ToString("D")),
            target => target.DisplayName);
    }

    private async Task<IReadOnlyList<IdentityResolvedPermissionTarget>> ResolveTargetsAsync(
        IEnumerable<IdentityPermissionTargetReference> references,
        CancellationToken cancellationToken)
    {
        var resolved = new List<IdentityResolvedPermissionTarget>();
        foreach (var batch in references.Distinct().Chunk(200))
        {
            resolved.AddRange(await _identityTargets.ResolveAsync(batch, cancellationToken));
        }
        return resolved;
    }

    private static IdentityPermissionTargetReference? TryCreateTargetReference(string providerName, string providerKey, Guid tenantId)
    {
        if (!Guid.TryParse(providerKey, out var id))
        {
            return null;
        }

        return providerName == PermissionProviderNames.Role
            ? new IdentityPermissionTargetReference(IdentityPermissionTargetKind.Role, id, tenantId)
            : providerName == PermissionProviderNames.User
                ? new IdentityPermissionTargetReference(IdentityPermissionTargetKind.User, id, tenantId)
                : null;
    }

    private void EnsureNotSelfReview(AccessReviewItem item, Guid reviewerUserId)
    {
        if (item.ProviderName == PermissionProviderNames.User &&
            Guid.TryParse(item.ProviderKey, out var userId) && userId == reviewerUserId)
        {
            throw new UnauthorizedAccessException("Reviewers cannot decide their own direct access.");
        }

        if (item.ProviderName != PermissionProviderNames.Role || !Guid.TryParse(item.ProviderKey, out var roleId))
        {
            return;
        }

        if (!ClaimValueParser.TryParseCanonicalGuidClaims(
                _currentUser.FindClaims(SharedClaimTypes.IdentityRoleId).Select(x => x.Value),
                32,
                out var roleIds))
        {
            throw new UnauthorizedAccessException("The reviewer role membership claim set is invalid.");
        }

        if (roleIds.Contains(roleId))
        {
            throw new UnauthorizedAccessException("Reviewers cannot decide access granted through one of their roles.");
        }
    }

    private static bool MatchesSnapshot(PermissionGrant? sourceGrant, AccessReviewItem item)
        => sourceGrant is not null &&
           sourceGrant.Id == item.SourceGrantId &&
           sourceGrant.ProviderName == item.ProviderName &&
           sourceGrant.ProviderKey == item.ProviderKey &&
           sourceGrant.Name == item.PermissionName &&
           sourceGrant.Effect == item.Effect &&
           sourceGrant.CreationTimeUtc == item.SourceGrantCreationTimeUtc &&
           sourceGrant.ExpiresAtUtc == item.ExpiresAtUtc &&
           sourceGrant.Reason == item.GrantReason &&
           sourceGrant.TicketId == item.TicketId &&
           sourceGrant.GrantedByUserId == item.GrantedByUserId;

    private Dictionary<string, string> BuildPermissionGroups()
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in _definitions.GetGroups())
        {
            foreach (var definition in Flatten(group.Permissions))
            {
                groups[definition.Name] = group.DisplayName ?? group.Name;
            }
        }
        return groups;
    }

    private static IEnumerable<Contracts.Definitions.PermissionDefinition> Flatten(
        IEnumerable<Contracts.Definitions.PermissionDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            yield return definition;
            foreach (var child in Flatten(definition.Children))
            {
                yield return child;
            }
        }
    }

    private static AccessReviewConflictException Conflict(
        AccessReviewConflictCode code,
        AccessReviewItem? item = null)
        => new(new AccessReviewConflictDto
        {
            Code = code,
            CurrentVersion = item?.Version,
            CurrentStatus = item?.Status
        });

    private static AccessReviewCampaignDto MapCampaign(
        AccessReviewCampaign campaign,
        IEnumerable<CampaignStatusCount> counts)
    {
        var byStatus = counts.ToDictionary(x => x.Status, x => x.Count);
        return new AccessReviewCampaignDto
        {
            Id = campaign.Id,
            TenantId = campaign.TenantId!.Value,
            Name = campaign.Name,
            SnapshotAtUtc = campaign.SnapshotAtUtc,
            DueAtUtc = campaign.DueAtUtc,
            Status = campaign.Status,
            ClosedAtUtc = campaign.ClosedAtUtc,
            PendingItemCount = byStatus.GetValueOrDefault(AccessReviewItemStatus.Pending),
            CertifiedItemCount = byStatus.GetValueOrDefault(AccessReviewItemStatus.Certified),
            RevokedItemCount = byStatus.GetValueOrDefault(AccessReviewItemStatus.Revoked),
            ExpiredItemCount = byStatus.GetValueOrDefault(AccessReviewItemStatus.Expired)
        };
    }

    private static AccessReviewItemDto MapItem(
        AccessReviewItem item,
        PermissionGrant? sourceGrant,
        string providerDisplayName,
        string permissionGroup)
        => new()
        {
            Id = item.Id,
            CampaignId = item.CampaignId,
            SourceGrantId = item.SourceGrantId,
            ProviderName = item.ProviderName,
            ProviderKey = item.ProviderKey,
            ProviderDisplayName = providerDisplayName,
            PermissionName = item.PermissionName,
            PermissionGroup = permissionGroup,
            Effect = item.Effect,
            SourceGrantCreationTimeUtc = item.SourceGrantCreationTimeUtc,
            ExpiresAtUtc = item.ExpiresAtUtc,
            GrantReason = item.GrantReason,
            TicketId = item.TicketId,
            Status = item.Status,
            HasChangedSinceSnapshot = !MatchesSnapshot(sourceGrant, item),
            DecidedByUserId = item.DecidedByUserId,
            DecidedAtUtc = item.DecidedAtUtc,
            DecisionReason = item.DecisionReason,
            Version = item.Version
        };

    private Guid RequireCurrentUserId()
        => _currentUser.Id ?? throw new UnauthorizedAccessException("An authenticated reviewer is required.");

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private sealed record CampaignStatusCount(Guid CampaignId, AccessReviewItemStatus Status, long Count);
}
