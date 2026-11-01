using Shared;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.CurrentUser;
using Shared.Data.ConnectionStrings;
using Shared.Data.Filtering;
using Shared.Permissions.Authorization;
using Shared.Security;
using Tenant.Application.Contracts.Tenants;
using Tenant.Domain.Tenants;

using Shared.AuditLogs.Auditing;
using Shared.EventBus.Messaging;
using Shared.Uow.Abstractions;
using Shared.EventContracts;
using Tenant.Domain.Shared.Enums;
using Tenant.Domain.Shared.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
namespace Tenant.Application.Tenants;

public class TenantAppService : ITenantAppService, ITransientDependency, IUnitOfWorkEnabled
{
    private static readonly string[] DedicatableDatabaseNames = [.. TenantWrightDatabaseCatalog.TenantDedicatable];

    private readonly IRepository<Domain.Tenants.Tenant, Guid> _tenantRepository;
    private readonly TenantManager _tenantManager;
    private readonly TenantCacheManager _tenantCacheManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly ITenantWrightMessageSender _messages;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;
    private readonly IDataFilter _dataFilter;
    private readonly IPermissionChecker _permissionChecker;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;
    private readonly TenantImpactEnrichmentClient? _impactEnrichmentClient;
    private readonly ICurrentUser? _currentUser;

    public TenantAppService(
        IRepository<Domain.Tenants.Tenant, Guid> tenantRepository, TenantManager tenantManager, TenantCacheManager tenantCacheManager, ICurrentTenant currentTenant, ITenantWrightMessageSender eventBus, IUnitOfWorkManager unitOfWorkManager, IDbContextProvider<TenantDbContextApp> dbContextProvider, IDataFilter dataFilter, IPermissionChecker permissionChecker, ICurrentPrincipalAccessor currentPrincipalAccessor, TenantImpactEnrichmentClient? impactEnrichmentClient = null, ICurrentUser? currentUser = null)
    {
        _tenantRepository = tenantRepository;
        _tenantManager = tenantManager;
        _tenantCacheManager = tenantCacheManager;
        _currentTenant = currentTenant;
        _messages = eventBus;
        _unitOfWorkManager = unitOfWorkManager;
        _dbContextProvider = dbContextProvider;
        _dataFilter = dataFilter;
        _permissionChecker = permissionChecker;
        _currentPrincipalAccessor = currentPrincipalAccessor;
        _impactEnrichmentClient = impactEnrichmentClient;
        _currentUser = currentUser;
    }

    public async Task<TenantDto> GetAsync(Guid id)
    {
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: false)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        return MapToDto(tenant);
    }

    public async Task<TenantDetailDto> GetDetailAsync(Guid id, bool includeDeleted = false)
    {
        // Host-only detail read. Supports inspecting a soft-deleted tenant (design
        // P1) before restoring it; the raw connection string is never returned.
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: includeDeleted)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        return MapToDetailDto(tenant);
    }

    public async Task<PagedResultDto<TenantListItemDto>> GetListAsync(GetTenantsInput input)
    {
        if (input.IncludeDeleted)
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                return await GetListInternalAsync(input);
            }
        }

        return await GetListInternalAsync(input);
    }

    public async Task<IReadOnlyList<TenantSelectionOptionDto>> GetSelectionOptionsAsync(
        GetTenantSelectionOptionsInput input,
        CancellationToken cancellationToken = default)
    {
        EnsureHostLifecycleManagement();
        ArgumentNullException.ThrowIfNull(input);

        var search = input.Search?.Trim();
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 50);
        var cachedTenants = await _tenantCacheManager.GetSelectionOptionsAsync(cancellationToken);
        var query = cachedTenants.AsEnumerable();

        if (!input.IncludeInactive)
        {
            query = query.Where(tenant => tenant.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = Guid.TryParse(search, out var tenantId)
                ? query.Where(tenant =>
                    tenant.Id == tenantId ||
                    tenant.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                : query.Where(tenant =>
                    tenant.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderByDescending(tenant =>
                !string.IsNullOrWhiteSpace(search) &&
                tenant.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .ThenBy(tenant => tenant.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tenant => tenant.Id)
            .Take(maxResultCount)
            .Select(tenant => new TenantSelectionOptionDto
            {
                Id = tenant.Id,
                Name = tenant.Name,
                IsActive = tenant.IsActive
            })
            .ToList();
    }

    private async Task<PagedResultDto<TenantListItemDto>> GetListInternalAsync(GetTenantsInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var query = (await _tenantRepository.GetQueryableAsync()).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(input.Filter))
        {
            // Substring match against NormalizedName (uppercased like tenant names
            // elsewhere): case-insensitive without a DB collation dependency, but a
            // sequential scan in PostgreSQL. Index-backed substring search at scale
            // would need a pg_trgm GIN index; not warranted at the tenant-table size.
            var normalizedFilter = input.Filter.Trim().ToUpperInvariant();
            query = query.Where(x => x.NormalizedName.Contains(normalizedFilter));
        }

        if (input.LifecycleStatuses is { Count: > 0 })
        {
            var statuses = input.LifecycleStatuses.ToArray();
            query = query.Where(x => statuses.Contains(
                !x.IsActive && x.LifecycleStatus == TenantLifecycleStatus.Active
                    ? TenantLifecycleStatus.Suspended
                    : x.LifecycleStatus));
        }

        if (input.IsActive.HasValue)
        {
            var isActive = input.IsActive.Value;
            query = query.Where(x => x.IsActive == isActive);
        }

        if (input.CreatedFromUtc.HasValue)
        {
            var createdFrom = input.CreatedFromUtc.Value;
            query = query.Where(x => x.CreationTime >= createdFrom);
        }

        if (input.CreatedToUtc.HasValue)
        {
            var createdTo = input.CreatedToUtc.Value;
            query = query.Where(x => x.CreationTime <= createdTo);
        }

        if (input.HasDedicatedDatabase.HasValue)
        {
            var hasDedicated = input.HasDedicatedDatabase.Value;
            query = query.Where(x => x.ConnectionStrings.Any(c => DedicatableDatabaseNames.Contains(c.Name)) == hasDedicated);
        }

        if (input.OwnerUserId.HasValue)
        {
            var ownerUserId = input.OwnerUserId.Value;
            query = query.Where(x => x.OwnerUserId == ownerUserId);
        }

        if (input.ProvisioningStage.HasValue)
        {
            query = ApplyProvisioningStageFilter(unitOfWorkDbContext, query, input.ProvisioningStage.Value);
        }

        var totalCount = await query.LongCountAsync();

        // Rejects an undeclared field instead of silently falling back to the default order.
        var rows = await ApplySort(query, SortingValidator.ValidateAndNormalize<GetTenantsInput>(input.Sorting))
            .Skip(Math.Max(0, input.SkipCount))
            .Take(Math.Clamp(input.MaxResultCount, 1, 1000))
            .Select(x => new TenantListRow
            {
                Id = x.Id,
                Name = x.Name,
                IsActive = x.IsActive,
                IsDeleted = x.IsDeleted,
                LifecycleStatus = x.LifecycleStatus,
                CreationTime = x.CreationTime,
                LastModificationTime = x.LastModificationTime,
                OwnerUserId = x.OwnerUserId,
                OwnerDisplayName = x.OwnerDisplayName,
                OwnerEmail = x.OwnerEmail,
                // EXISTS subquery: does not load the connection-string child rows.
                HasDedicatedDatabase = x.ConnectionStrings.Any(c => DedicatableDatabaseNames.Contains(c.Name))
            })
            .ToListAsync();

        // Resolve the caller's action grants once (batch), then compute per-row
        // AllowedActions in memory — no N+1 permission checks on the hot list path.
        var grants = await _permissionChecker.IsGrantedAsync(
            _currentPrincipalAccessor.Principal,
            TenantAllowedActionsCalculator.RequiredPermissions);

        return new PagedResultDto<TenantListItemDto>
        {
            TotalCount = totalCount,
            Items = rows.Select(row => MapToListItem(row, grants)).ToList()
        };
    }

    private static IQueryable<Domain.Tenants.Tenant> ApplyProvisioningStageFilter(
TenantDbContextApp unitOfWorkDbContext, IQueryable<Domain.Tenants.Tenant> query,
        TenantProvisioningStage stage)
    {
        // Active is a tenant-state fact, independent of any surviving request row.
        if (stage == TenantProvisioningStage.Active)
        {
            return query.Where(t => t.IsActive);
        }

        var initialOwnerRequests = unitOfWorkDbContext.TenantOwnershipRequests
            .Where(r => r.Kind == TenantOwnershipRequestKind.InitialOwner);

        // Classify by the furthest milestone reached without advancing to the next.
        var stagedRequests = stage switch
        {
            TenantProvisioningStage.Created =>
                initialOwnerRequests.Where(r => r.InvitationCreatedAtUtc == null),
            TenantProvisioningStage.InvitationSent =>
                initialOwnerRequests.Where(r => r.InvitationCreatedAtUtc != null && r.CompletedAtUtc == null),
            TenantProvisioningStage.OwnerVerified =>
                initialOwnerRequests.Where(r => r.CompletedAtUtc != null && r.PermissionsProvisionedAtUtc == null),
            _ =>
                initialOwnerRequests.Where(r => r.PermissionsProvisionedAtUtc != null)
        };

        // In-progress stages exclude tenants that already went live, so the drill
        // surfaces only the cohort still stuck at that milestone.
        return query.Where(t => !t.IsActive && stagedRequests.Any(r => r.TenantId == t.Id));
    }

    private static IQueryable<Domain.Tenants.Tenant> ApplySort(
        IQueryable<Domain.Tenants.Tenant> query,
        string? sorting)
    {
        // Each branch adds Id as a final tiebreaker so offset paging is deterministic. A bare field
        // name means ascending; `SortableFieldsAttribute` on GetTenantsInput is the whitelist, so this
        // mapping must stay complete for every declared field.
        return sorting?.Trim().ToLowerInvariant() switch
        {
            "creationtime" or "creationtime asc" => query.OrderBy(x => x.CreationTime).ThenBy(x => x.Id),
            "creationtime desc" => query.OrderByDescending(x => x.CreationTime).ThenBy(x => x.Id),
            "lifecyclestatus" or "lifecyclestatus asc" =>
                query.OrderBy(x => x.LifecycleStatus).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "lifecyclestatus desc" =>
                query.OrderByDescending(x => x.LifecycleStatus).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "owner" or "owner asc" =>
                query.OrderBy(x => x.OwnerDisplayName).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "owner desc" =>
                query.OrderByDescending(x => x.OwnerDisplayName).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "name desc" => query.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            _ => query.OrderBy(x => x.Name).ThenBy(x => x.Id)
        };
    }

    private static TenantListItemDto MapToListItem(TenantListRow row, IReadOnlyDictionary<string, bool> grants)
    {
        // Effective lifecycle status mirrors Tenant.GetEffectiveLifecycleStatus(): an
        // inactive tenant still stored as Active is surfaced as Suspended. The list
        // predicate applies the same normalization before filtering.
        var effectiveStatus = !row.IsActive && row.LifecycleStatus == TenantLifecycleStatus.Active
            ? TenantLifecycleStatus.Suspended
            : row.LifecycleStatus;

        var owner = row.OwnerUserId.HasValue || row.OwnerDisplayName != null || row.OwnerEmail != null
            ? new TenantOwnerSummaryDto
            {
                UserId = row.OwnerUserId,
                DisplayName = row.OwnerDisplayName,
                Email = row.OwnerEmail
            }
            : null;

        return new TenantListItemDto
        {
            Id = row.Id,
            Name = row.Name,
            IsActive = row.IsActive,
            IsDeleted = row.IsDeleted,
            LifecycleStatus = effectiveStatus,
            CreationTime = row.CreationTime,
            LastModificationTime = row.LastModificationTime,
            Owner = owner,
            // A flag only on the list; per-database status and fingerprints stay on the detail read.
            HasDedicatedDatabase = row.HasDedicatedDatabase,
            AllowedActions = TenantAllowedActionsCalculator.Compute(row.LifecycleStatus, row.IsDeleted, row.OwnerUserId, grants)
        };
    }

    private sealed class TenantListRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;
        public bool IsActive { get; set; }
        public bool IsDeleted { get; set; }
        public TenantLifecycleStatus LifecycleStatus { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime? LastModificationTime { get; set; }
        public Guid? OwnerUserId { get; set; }
        public string? OwnerDisplayName { get; set; }
        public string? OwnerEmail { get; set; }
        public bool HasDedicatedDatabase { get; set; }
    }

    public async Task<CurrentTenantProfileDto> GetCurrentProfileAsync()
    {
        var tenantId = _currentTenant.Id ?? throw new SharedValidationException("Tenant context is required.");
        var tenant = await _tenantRepository.FindAsync(x => x.Id == tenantId, includeDetails: false);

        if (tenant == null)
        {
            throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), tenantId);
        }

        return new CurrentTenantProfileDto
        {
            Id = tenant.Id,
            Name = tenant.Name,
            IsActive = tenant.IsActive,
            LifecycleStatus = tenant.GetEffectiveLifecycleStatus()
        };
    }

    public async Task<TenantOrganizationProfileDto> GetOrganizationProfileAsync(Guid id)
    {
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        if (_currentTenant.Id.HasValue && (_currentTenant.Id != id || tenant.OwnerUserId != _currentUser?.Id)) throw new UnauthorizedAccessException("Only the Tenant Owner can read this profile.");
        return MapOrganizationProfile(tenant);
    }

    public Task<TenantOrganizationProfileDto> GetCurrentOrganizationProfileAsync()
    {
        var tenantId = _currentTenant.Id ?? throw new SharedValidationException("Tenant context is required.");
        return GetOrganizationProfileAsync(tenantId);
    }

    [Audited(Category = "tenant.profile")]
    public async Task<TenantOrganizationProfileDto> UpdateOrganizationProfileAsync(Guid id, TenantOrganizationProfileUpdateInput input)
    {
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        if (_currentTenant.Id.HasValue && (_currentTenant.Id != id || tenant.OwnerUserId != _currentUser?.Id)) throw new UnauthorizedAccessException("Only the Tenant Owner can update this profile.");
        EnsureConcurrency(tenant, input.ConcurrencyStamp);
        tenant.SetOrganizationProfile(input.ContactName, input.ContactEmail, input.ContactPhone, input.AddressLine1, input.AddressLine2, input.City, input.StateOrUnionTerritory, input.PinCode);
        await _tenantRepository.UpdateAsync(tenant, autoSave: ShouldAutoSave());
        await _messages.PublishEventAsync(new TenantOrganizationProfileChangedEto { TenantId = id, ChangedFields = ["organizationProfile"], OccurredAtUtc = DateTime.UtcNow });
        return MapOrganizationProfile(tenant);
    }

    public Task<TenantOrganizationProfileDto> UpdateCurrentOrganizationProfileAsync(TenantOrganizationProfileUpdateInput input)
    {
        var tenantId = _currentTenant.Id ?? throw new SharedValidationException("Tenant context is required.");
        return UpdateOrganizationProfileAsync(tenantId, input);
    }

    public async Task<TenantLegalComplianceProfileDto> GetLegalComplianceProfileAsync(Guid id)
    {
        EnsureHostProfileScope();
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        return MapLegalComplianceProfile(tenant);
    }

    [Audited(Category = "tenant.compliance")]
    public async Task<TenantLegalComplianceProfileDto> UpdateLegalComplianceProfileAsync(Guid id, TenantLegalComplianceProfileUpdateInput input)
    {
        EnsureHostProfileScope();
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        EnsureConcurrency(tenant, input.ConcurrencyStamp);
        tenant.SetLegalComplianceProfile(input.LegalEntityName, input.RegistrationNumber, input.TaxId, input.Accreditation);
        await _tenantRepository.UpdateAsync(tenant, autoSave: ShouldAutoSave());
        await _messages.PublishEventAsync(new TenantOrganizationProfileChangedEto { TenantId = id, ChangedFields = ["legalCompliance"], OccurredAtUtc = DateTime.UtcNow });
        return MapLegalComplianceProfile(tenant);
    }

    public async Task<TenantDataGovernanceProfileDto> GetDataGovernanceProfileAsync(Guid id)
    {
        EnsureHostProfileScope();
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        return MapDataGovernanceProfile(tenant);
    }

    [Audited(Category = "tenant.data-governance")]
    public async Task<TenantDataGovernanceProfileDto> UpdateDataGovernanceProfileAsync(Guid id, TenantDataGovernanceProfileUpdateInput input)
    {
        EnsureHostProfileScope();
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        EnsureConcurrency(tenant, input.ConcurrencyStamp);
        tenant.SetDataGovernanceProfile(input.DataRegion, input.RetentionPolicyDays);
        await _tenantRepository.UpdateAsync(tenant, autoSave: ShouldAutoSave());
        await _messages.PublishEventAsync(new TenantOrganizationProfileChangedEto { TenantId = id, ChangedFields = ["dataGovernance"], OccurredAtUtc = DateTime.UtcNow });
        return MapDataGovernanceProfile(tenant);
    }

    private void EnsureHostProfileScope()
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Tenant compliance and data governance require host scope.");
        }
    }

    private static TenantOrganizationProfileDto MapOrganizationProfile(Domain.Tenants.Tenant x) => new() { TenantId = x.Id, ContactName = x.ContactName, ContactEmail = x.ContactEmail, ContactPhone = x.ContactPhone, AddressLine1 = x.AddressLine1, AddressLine2 = x.AddressLine2, City = x.City, StateOrUnionTerritory = x.StateOrUnionTerritory, PinCode = x.PinCode, ConcurrencyStamp = x.ConcurrencyStamp };
    private static TenantLegalComplianceProfileDto MapLegalComplianceProfile(Domain.Tenants.Tenant x) => new() { TenantId = x.Id, LegalEntityName = x.LegalEntityName, RegistrationNumber = x.RegistrationNumber, TaxId = x.TaxId, Accreditation = x.Accreditation, ConcurrencyStamp = x.ConcurrencyStamp };
    private static TenantDataGovernanceProfileDto MapDataGovernanceProfile(Domain.Tenants.Tenant x) => new() { TenantId = x.Id, DataRegion = x.DataRegion, RetentionPolicyDays = x.RetentionPolicyDays, ConcurrencyStamp = x.ConcurrencyStamp };


    [Audited]
    public async Task<TenantDto> CreateAsync(TenantCreateDto input)
    {
        var tenant = await _tenantManager.CreateAsync(input.Name);
        if (!input.IsActive)
        {
            tenant.Deactivate();
        }

        await _tenantRepository.InsertAsync(tenant, autoSave: ShouldAutoSave());
        await ScheduleTenantCacheUpsertAfterCommitAsync(tenant);

        return MapToDto(tenant);
    }

    [Audited]
    public async Task<TenantOwnershipRequestDto> CreateWithInitialOwnerAsync(TenantCreateWithInitialOwnerInput input)
    {
        EnsureHostLifecycleManagement();
        var (tenant, request) = await CreateInitialOwnerProvisioningAsync(
            input.Name, input.OwnerEmail, input.SuggestedUserName, input.ReasonCode, input.Note,
            TenantRegistrationOrigin.SuperAdmin, null);
        return new TenantOwnershipRequestDto { Id = request.Id, TenantId = tenant.Id, TargetEmail = request.TargetEmail, Kind = request.Kind, Status = request.Status };
    }

    [Audited]
    public async Task<PublicTenantRegistrationInitiateResult> InitiatePublicRegistrationAsync(PublicTenantRegistrationInitiateInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (input.OperationId == Guid.Empty) throw new SharedValidationException("Registration operation id is required.");
        var existing = await unitOfWorkDbContext.TenantOwnershipRequests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.RegistrationOperationId == input.OperationId);
        if (existing != null)
            return new PublicTenantRegistrationInitiateResult { TenantId = existing.TenantId, OwnershipRequestId = existing.Id, IsExistingOperation = true };

        var (tenant, request) = await CreateInitialOwnerProvisioningAsync(
            input.Name, input.OwnerEmail, input.SuggestedUserName, "public-registration", null,
            TenantRegistrationOrigin.PublicAuth, input.OperationId);
        return new PublicTenantRegistrationInitiateResult { TenantId = tenant.Id, OwnershipRequestId = request.Id, IsExistingOperation = false };
    }

    public async Task<TenantProvisioningOperationDto> GetProvisioningOperationAsync(Guid tenantId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var tenant = await unitOfWorkDbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == tenantId)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), tenantId);
        var request = await unitOfWorkDbContext.TenantOwnershipRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Kind == TenantOwnershipRequestKind.InitialOwner)
            .OrderByDescending(x => x.CreationTime)
            .FirstOrDefaultAsync()
            ?? throw new EntityNotFoundException(typeof(TenantOwnershipRequest), tenantId);

        return MapProvisioningOperation(request, tenant, DateTime.UtcNow);
    }

    public async Task<TenantProvisioningOperationDto?> GetPublicRegistrationAsync(Guid operationId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (operationId == Guid.Empty)
            throw new SharedValidationException("Registration operation id is required.");

        var request = await unitOfWorkDbContext.TenantOwnershipRequests.AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.RegistrationOperationId == operationId &&
                x.RegistrationOrigin == TenantRegistrationOrigin.PublicAuth &&
                x.Kind == TenantOwnershipRequestKind.InitialOwner);
        if (request == null)
            return null;

        var tenant = await unitOfWorkDbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.TenantId);
        return tenant == null ? null : MapProvisioningOperation(request, tenant, DateTime.UtcNow);
    }

    [Audited(Category = "tenant.provisioning")]
    public async Task<TenantProvisioningOperationDto> RetryProvisioningOperationAsync(
        Guid tenantId,
        TenantProvisioningRetryInput input)
    {
        EnsureHostLifecycleManagement();
        ArgumentNullException.ThrowIfNull(input);
        return await RetryProvisioningAsync(tenantId, operationId: null, input.ReasonCode, input.Note);
    }

    [Audited(Category = "tenant.public-registration")]
    public Task<TenantProvisioningOperationDto> RetryPublicRegistrationAsync(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new SharedValidationException("Registration operation id is required.");

        return RetryProvisioningAsync(
            tenantId: null,
            operationId,
            reasonCode: "internal-public-registration-retry",
            note: null);
    }

    private async Task<TenantProvisioningOperationDto> RetryProvisioningAsync(
        Guid? tenantId,
        Guid? operationId,
        string reasonCode,
        string? note)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var requestQuery = unitOfWorkDbContext.TenantOwnershipRequests.Where(x =>
                    x.Kind == TenantOwnershipRequestKind.InitialOwner);
        requestQuery = tenantId.HasValue
            ? requestQuery.Where(x => x.TenantId == tenantId.Value)
            : requestQuery.Where(x =>
                x.RegistrationOperationId == operationId &&
                x.RegistrationOrigin == TenantRegistrationOrigin.PublicAuth);

        var request = await requestQuery
            .OrderByDescending(x => x.CreationTime)
            .FirstOrDefaultAsync()
            ?? throw new EntityNotFoundException(typeof(TenantOwnershipRequest), tenantId ?? operationId!.Value);
        var tenant = await unitOfWorkDbContext.Tenants.FirstOrDefaultAsync(x => x.Id == request.TenantId)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), request.TenantId);

        if (tenant.GetEffectiveLifecycleStatus() != TenantLifecycleStatus.Provisioning || tenant.OwnerUserId.HasValue)
            throw new SharedValidationException("Only an ownerless tenant still in provisioning can retry its initial-owner handoff.");

        var now = DateTime.UtcNow;
        try
        {
            request.RecordProvisioningRetry(now, reasonCode, note);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new SharedValidationException(exception.Message);
        }

        await _messages.PublishEventAsync(BuildOwnershipInvitationRequestedEvent(request, tenant, now));

        return MapProvisioningOperation(request, tenant, now);
    }

    private static TenantOwnershipInvitationRequestedEto BuildOwnershipInvitationRequestedEvent(
        TenantOwnershipRequest request,
        Domain.Tenants.Tenant tenant,
        DateTime occurredAtUtc,
        bool isResend = false)
        => new()
        {
            RequestId = request.Id,
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            TargetEmail = request.TargetEmail,
            SuggestedUserName = request.SuggestedUserName,
            Kind = ToContract(request.Kind),
            IsResend = isResend,
            OccurredAtUtc = occurredAtUtc
        };

    private async Task<(Domain.Tenants.Tenant Tenant, TenantOwnershipRequest Request)> CreateInitialOwnerProvisioningAsync(
        string name, string ownerEmail, string? suggestedUserName, string reasonCode, string? note,
        TenantRegistrationOrigin origin, Guid? operationId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenant = await _tenantManager.CreateAsync(name);
        tenant.BeginProvisioning();
        var email = Shared.Check.NotNullOrWhiteSpace(ownerEmail, nameof(ownerEmail), maxLength: 256).Trim();
        reasonCode = Shared.Check.NotNullOrWhiteSpace(reasonCode, nameof(reasonCode), maxLength: 64).Trim();
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > 500) throw new SharedValidationException("Ownership request note must be 500 characters or fewer.");
        var request = new TenantOwnershipRequest(Guid.CreateVersion7(), tenant.Id, null, email, suggestedUserName?.Trim(), reasonCode, note,
            TenantOwnershipRequestKind.InitialOwner, registrationOrigin: origin, registrationOperationId: operationId);
        await _tenantRepository.InsertAsync(tenant, autoSave: ShouldAutoSave());
        await unitOfWorkDbContext.TenantOwnershipRequests.AddAsync(request);
        await _messages.SendCommandAsync(new TenantInitialOwnerOnboardingRequestedEto
        {
            RequestId = request.Id,
            TenantId = tenant.Id,
            ExpiresAtUtc = request.ExpiresAtUtc,
            OccurredAtUtc = DateTime.UtcNow
        });
        await _messages.PublishEventAsync(new TenantOwnershipInvitationRequestedEto
        {
            RequestId = request.Id,
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            TargetEmail = email,
            SuggestedUserName = request.SuggestedUserName,
            Kind = TenantOwnershipRequestKindEto.InitialOwner,
            OccurredAtUtc = DateTime.UtcNow
        });
        // Persist the tenant state and the outbox message together, in the unit of
        // work's transaction, before the request is acknowledged.
        await unitOfWorkDbContext.SaveChangesAsync();
        await ScheduleTenantCacheUpsertAfterCommitAsync(tenant);
        return (tenant, request);
    }

    [Audited]
    public async Task<int> CleanupExpiredPublicRegistrationsAsync(DateTime utcNow, int expiryHours, int batchSize, CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var cutoff = utcNow.AddHours(-Math.Clamp(expiryHours, 24, 24 * 30));
        var candidates = await (from request in unitOfWorkDbContext.TenantOwnershipRequests
                                join tenant in unitOfWorkDbContext.Tenants on request.TenantId equals tenant.Id
                                where request.Kind == TenantOwnershipRequestKind.InitialOwner
                                   && request.RegistrationOrigin == TenantRegistrationOrigin.PublicAuth
                                   && request.Status != TenantOwnershipRequestStatus.Completed
                                   && request.CreationTime <= cutoff
                                   && tenant.LifecycleStatus == TenantLifecycleStatus.Provisioning
                                   && tenant.OwnerUserId == null
                                orderby request.CreationTime
                                select new { Request = request, Tenant = tenant })
            .Take(Math.Clamp(batchSize, 1, 100))
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            candidate.Request.ExpireIfDue(utcNow);
            candidate.Request.Cancel();
            await _messages.PublishEventAsync(new TenantOwnershipInvitationCanceledEto
            {
                RequestId = candidate.Request.Id,
                TenantId = candidate.Tenant.Id,
                OccurredAtUtc = utcNow
            }, cancellationToken);
            unitOfWorkDbContext.TenantOwnershipRequests.Remove(candidate.Request);
            await _tenantRepository.DeleteAsync(candidate.Tenant, autoSave: false, cancellationToken: cancellationToken);
            await ExecuteAfterCommitAsync(() => _tenantCacheManager.RemoveTenantAsync(candidate.Tenant.Id, candidate.Tenant.NormalizedName));
        }
        return candidates.Count;
    }

    [Audited]
    public async Task<TenantOwnershipRequestDto> RequestOwnershipTransferAsync(Guid id, TenantOwnershipTransferInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenant = await FindTenantAsync(id, includeDetails: false, includeDeleted: false)
                    ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        var isHostRecovery = !_currentTenant.Id.HasValue;
        if (!isHostRecovery && (_currentTenant.Id != id || _currentUser is null || !_currentUser.Id.HasValue || tenant.OwnerUserId != _currentUser.Id))
            throw new UnauthorizedAccessException("Only the current tenant owner can transfer ownership.");
        var reasonCode = Shared.Check.NotNullOrWhiteSpace(input.ReasonCode, nameof(input.ReasonCode), maxLength: 64).Trim();
        if (isHostRecovery && string.IsNullOrWhiteSpace(reasonCode)) throw new SharedValidationException("A recovery reason is required.");
        var email = Shared.Check.NotNullOrWhiteSpace(input.TargetEmail, nameof(input.TargetEmail), maxLength: 256).Trim();
        var existing = await unitOfWorkDbContext.TenantOwnershipRequests.AnyAsync(x => x.TenantId == id && (x.Status == TenantOwnershipRequestStatus.PendingInvitation || x.Status == TenantOwnershipRequestStatus.AwaitingEmailVerification));
        if (existing) throw new SharedValidationException("A tenant ownership request is already active.");
        var request = new TenantOwnershipRequest(Guid.CreateVersion7(), id, tenant.OwnerUserId, email, input.SuggestedUserName?.Trim(), reasonCode, input.Note?.Trim(), isHostRecovery ? TenantOwnershipRequestKind.Recovery : TenantOwnershipRequestKind.Transfer);
        await unitOfWorkDbContext.TenantOwnershipRequests.AddAsync(request);
        await unitOfWorkDbContext.SaveChangesAsync();
        await _messages.PublishEventAsync(new TenantOwnershipInvitationRequestedEto { RequestId = request.Id, TenantId = id, TenantName = tenant.Name, TargetEmail = email, SuggestedUserName = request.SuggestedUserName, Kind = ToContract(request.Kind), OccurredAtUtc = DateTime.UtcNow });
        return MapOwnershipRequest(request);
    }

    public async Task<PagedResultDto<TenantOwnershipRequestDto>> GetOwnershipHistoryAsync(Guid id, GetTenantOwnershipHistoryInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var now = DateTime.UtcNow;
        var requests = unitOfWorkDbContext.TenantOwnershipRequests.Where(x => x.TenantId == id);
        var items = await requests.OrderByDescending(x => x.CreationTime)
            .Skip(Math.Max(0, input.SkipCount)).Take(Math.Clamp(input.MaxResultCount, 1, 100))
            .ToListAsync();
        var expiringIds = items.Where(x => (x.Status is TenantOwnershipRequestStatus.PendingInvitation or TenantOwnershipRequestStatus.AwaitingEmailVerification) && x.ExpiresAtUtc <= now).Select(x => x.Id).ToList();
        foreach (var request in items) request.ExpireIfDue(now);
        await unitOfWorkDbContext.SaveChangesAsync();
        foreach (var requestId in expiringIds)
            await _messages.PublishEventAsync(new TenantOwnershipInvitationCanceledEto { RequestId = requestId, TenantId = id, OccurredAtUtc = now });
        return new PagedResultDto<TenantOwnershipRequestDto>
        {
            TotalCount = await requests.LongCountAsync(),
            Items = items.Select(MapOwnershipRequest).ToList()
        };
    }

    [Audited]
    public async Task CancelOwnershipRequestAsync(Guid id, Guid requestId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var request = await unitOfWorkDbContext.TenantOwnershipRequests.FirstOrDefaultAsync(x => x.Id == requestId && x.TenantId == id)
            ?? throw new EntityNotFoundException(typeof(TenantOwnershipRequest), requestId);
        if (request.Status == TenantOwnershipRequestStatus.Completed) throw new SharedValidationException("Completed ownership requests cannot be canceled.");
        request.Cancel();
        await unitOfWorkDbContext.SaveChangesAsync();
        await _messages.PublishEventAsync(new TenantOwnershipInvitationCanceledEto { RequestId = request.Id, TenantId = id, OccurredAtUtc = DateTime.UtcNow });
    }

    [Audited(Category = "tenant.ownership")]
    public async Task<TenantOwnershipRequestDto> ResendOwnershipRequestAsync(Guid id, Guid requestId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, includeDetails: false, includeDeleted: false)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        var request = await unitOfWorkDbContext.TenantOwnershipRequests.FirstOrDefaultAsync(x => x.Id == requestId && x.TenantId == id)
            ?? throw new EntityNotFoundException(typeof(TenantOwnershipRequest), requestId);
        var now = DateTime.UtcNow;
        try
        {
            request.RecordResend(now);
        }
        catch (InvalidOperationException exception)
        {
            throw new SharedValidationException(exception.Message);
        }

        await _messages.PublishEventAsync(BuildOwnershipInvitationRequestedEvent(request, tenant, now, isResend: true));
        await unitOfWorkDbContext.SaveChangesAsync();
        return MapOwnershipRequest(request);
    }

    private static TenantOwnershipRequestDto MapOwnershipRequest(TenantOwnershipRequest request) => new()
    {
        Id = request.Id,
        TenantId = request.TenantId,
        TargetEmail = request.TargetEmail,
        Kind = request.Kind,
        Status = request.Status,
        PreviousOwnerUserId = request.PreviousOwnerUserId,
        TargetUserId = request.TargetUserId,
        CreationTime = request.CreationTime,
        ExpiresAtUtc = request.ExpiresAtUtc,
        CompletedAtUtc = request.CompletedAtUtc,
        RegistrationOrigin = request.RegistrationOrigin,
        RegistrationOperationId = request.RegistrationOperationId,
        ResendCount = request.ResendCount,
        LastResentAtUtc = request.LastResentAtUtc,
        ResendAvailableAtUtc = request.LastResentAtUtc?.Add(TenantOwnershipRequest.MinimumResendInterval)
    };

    private static TenantProvisioningOperationDto MapProvisioningOperation(
        TenantOwnershipRequest request,
        Domain.Tenants.Tenant tenant,
        DateTime utcNow)
    {
        var effectiveStatus = request.Status;
        if (request.ExpiresAtUtc <= utcNow &&
            effectiveStatus is TenantOwnershipRequestStatus.PendingInvitation or TenantOwnershipRequestStatus.AwaitingEmailVerification)
            effectiveStatus = TenantOwnershipRequestStatus.Expired;

        var retryAvailableAtUtc = request.LastProvisioningRetryAtUtc?.Add(TenantOwnershipRequest.MinimumProvisioningRetryInterval);
        var canRetry = request.Kind == TenantOwnershipRequestKind.InitialOwner &&
                       effectiveStatus == TenantOwnershipRequestStatus.PendingInvitation &&
                       !request.IdentityInvitationId.HasValue &&
                       request.ProvisioningRetryCount < TenantOwnershipRequest.MaxProvisioningRetryCount &&
                       (!retryAvailableAtUtc.HasValue || retryAvailableAtUtc <= utcNow) &&
                       tenant.GetEffectiveLifecycleStatus() == TenantLifecycleStatus.Provisioning &&
                       !tenant.OwnerUserId.HasValue;

        return new TenantProvisioningOperationDto
        {
            TenantId = tenant.Id,
            OwnershipRequestId = request.Id,
            RegistrationOperationId = request.RegistrationOperationId,
            IdentityInvitationId = request.IdentityInvitationId,
            RegistrationOrigin = request.RegistrationOrigin,
            Status = effectiveStatus,
            TenantLifecycleStatus = tenant.GetEffectiveLifecycleStatus(),
            CreatedAtUtc = request.CreationTime,
            ExpiresAtUtc = request.ExpiresAtUtc,
            InvitationCreatedAtUtc = request.InvitationCreatedAtUtc,
            CompletedAtUtc = request.CompletedAtUtc,
            ProvisioningRetryCount = request.ProvisioningRetryCount,
            LastProvisioningRetryAtUtc = request.LastProvisioningRetryAtUtc,
            PermissionsProvisionedAtUtc = request.PermissionsProvisionedAtUtc,
            RetryAvailableAtUtc = retryAvailableAtUtc,
            CanRetry = canRetry,
            IsReady = effectiveStatus == TenantOwnershipRequestStatus.Completed &&
                request.PermissionsProvisionedAtUtc.HasValue &&
                tenant.GetEffectiveLifecycleStatus() == TenantLifecycleStatus.Active,
            IsTerminal = effectiveStatus is TenantOwnershipRequestStatus.Canceled or
                TenantOwnershipRequestStatus.Expired or TenantOwnershipRequestStatus.Failed ||
                effectiveStatus == TenantOwnershipRequestStatus.Completed &&
                request.PermissionsProvisionedAtUtc.HasValue &&
                tenant.GetEffectiveLifecycleStatus() == TenantLifecycleStatus.Active
        };
    }

    [Audited]
    public async Task<TenantClosureRequestDto> RequestClosureAsync(Guid id, TenantClosureRequestInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        var actorId = _currentUser?.Id ?? throw new UnauthorizedAccessException("Authenticated user context is required.");
        if (_currentTenant.Id.HasValue && (_currentTenant.Id != id || tenant.OwnerUserId != actorId))
            throw new UnauthorizedAccessException("Only the Tenant Owner can request closure.");
        var active = await unitOfWorkDbContext.TenantClosureRequests.AnyAsync(x => x.TenantId == id && (x.Status == TenantClosureStatus.Requested || x.Status == TenantClosureStatus.Approved));
        if (active) throw new SharedValidationException("A tenant closure request is already active.");
        var reason = Shared.Check.NotNullOrWhiteSpace(input.ReasonCode, nameof(input.ReasonCode), maxLength: 64).Trim();
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(); if (note?.Length > 500) throw new SharedValidationException("Closure note must be 500 characters or fewer.");
        var request = new TenantClosureRequest(Guid.CreateVersion7(), id, actorId, reason, note);
        await unitOfWorkDbContext.TenantClosureRequests.AddAsync(request);
        await unitOfWorkDbContext.SaveChangesAsync();
        return MapClosureRequest(request);
    }

    public async Task<TenantClosureStatusDto?> GetCurrentClosureRequestAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.Id ?? throw new SharedValidationException("Current tenant context is required.");
        var actorId = _currentUser?.Id ?? throw new UnauthorizedAccessException("Authenticated user context is required.");
        var tenant = await FindTenantAsync(tenantId, false, false)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), tenantId);
        if (tenant.OwnerUserId != actorId)
        {
            throw new UnauthorizedAccessException("Only the persisted Tenant Owner can read closure status.");
        }

        return await unitOfWorkDbContext.TenantClosureRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreationTime)
            .ThenByDescending(x => x.Id)
            .Select(x => new TenantClosureStatusDto
            {
                RequestId = x.Id,
                Status = x.Status,
                ReasonCode = x.ReasonCode,
                Note = x.Note,
                CreationTime = x.CreationTime,
                RejectedAtUtc = x.RejectedAtUtc,
                DecisionReasonCode = x.DecisionReasonCode,
                DecisionNote = x.DecisionNote,
                ClosedAtUtc = x.ClosedAtUtc,
                RestoredAtUtc = x.RestoredAtUtc
            })
            .FirstOrDefaultAsync();
    }

    [Audited]
    public async Task<TenantClosureRequestDto> ApproveClosureAsync(Guid id, Guid requestId, TenantClosureActionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var actorId = _currentUser?.Id ?? throw new UnauthorizedAccessException("Authenticated user context is required.");
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        var request = await unitOfWorkDbContext.TenantClosureRequests.FirstOrDefaultAsync(x => x.Id == requestId && x.TenantId == id) ?? throw new EntityNotFoundException(typeof(TenantClosureRequest), requestId);
        var reason = Shared.Check.NotNullOrWhiteSpace(input.ReasonCode, nameof(input.ReasonCode), maxLength: 64).Trim();
        request.Approve(actorId, DateTime.UtcNow); tenant.TransitionTo(TenantLifecycleStatus.Suspended); request.Close(DateTime.UtcNow);
        unitOfWorkDbContext.TenantLifecycleTransitions.Add(new TenantLifecycleTransition(Guid.CreateVersion7(), id, TenantLifecycleStatus.Active, TenantLifecycleStatus.Suspended, reason, input.Note?.Trim()));
        await _messages.PublishEventAsync(new TenantDeactivatedEto { TenantId = id, TenantName = tenant.Name, OccurredAtUtc = DateTime.UtcNow });
        await _messages.PublishEventAsync(new TenantClosureChangedEto { RequestId = request.Id, TenantId = id, OwnerUserId = tenant.OwnerUserId, IsClosed = true, ReasonCode = reason, OccurredAtUtc = DateTime.UtcNow });
        await unitOfWorkDbContext.SaveChangesAsync(); await ExecuteAfterCommitAsync(() => _tenantCacheManager.UpsertTenantAsync(id));
        return MapClosureRequest(request);
    }

    [Audited]
    public async Task<TenantClosureRequestDto> RestoreClosureAsync(Guid id, Guid requestId, TenantClosureActionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        var request = await unitOfWorkDbContext.TenantClosureRequests.FirstOrDefaultAsync(x => x.Id == requestId && x.TenantId == id) ?? throw new EntityNotFoundException(typeof(TenantClosureRequest), requestId);
        var reason = Shared.Check.NotNullOrWhiteSpace(input.ReasonCode, nameof(input.ReasonCode), maxLength: 64).Trim();
        request.Restore(DateTime.UtcNow); tenant.TransitionTo(TenantLifecycleStatus.Active);
        unitOfWorkDbContext.TenantLifecycleTransitions.Add(new TenantLifecycleTransition(Guid.CreateVersion7(), id, TenantLifecycleStatus.Suspended, TenantLifecycleStatus.Active, reason, input.Note?.Trim()));
        await _messages.PublishEventAsync(new TenantClosureChangedEto { RequestId = request.Id, TenantId = id, OwnerUserId = tenant.OwnerUserId, IsClosed = false, ReasonCode = reason, OccurredAtUtc = DateTime.UtcNow });
        await unitOfWorkDbContext.SaveChangesAsync(); await ExecuteAfterCommitAsync(() => _tenantCacheManager.UpsertTenantAsync(id));
        return MapClosureRequest(request);
    }

    [Audited]
    public async Task<TenantClosureRequestDto> RejectClosureAsync(Guid id, Guid requestId, TenantClosureActionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var actorId = _currentUser?.Id ?? throw new UnauthorizedAccessException("Authenticated user context is required.");
        _ = await FindTenantAsync(id, false, false) ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        var request = await unitOfWorkDbContext.TenantClosureRequests.FirstOrDefaultAsync(x => x.Id == requestId && x.TenantId == id)
            ?? throw new EntityNotFoundException(typeof(TenantClosureRequest), requestId);
        var reason = Shared.Check.NotNullOrWhiteSpace(input.ReasonCode, nameof(input.ReasonCode), maxLength: 64).Trim();
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note?.Length > 500) throw new SharedValidationException("Closure decision note must be 500 characters or fewer.");
        request.Reject(actorId, DateTime.UtcNow, reason, note);
        await unitOfWorkDbContext.SaveChangesAsync();
        return MapClosureRequest(request);
    }

    [Audited]
    public async Task CancelClosureAsync(Guid id, Guid requestId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var request = await unitOfWorkDbContext.TenantClosureRequests.FirstOrDefaultAsync(x => x.Id == requestId && x.TenantId == id) ?? throw new EntityNotFoundException(typeof(TenantClosureRequest), requestId);
        if (_currentTenant.Id.HasValue && (_currentTenant.Id != id || _currentUser?.Id != request.RequestedByUserId)) throw new UnauthorizedAccessException("Only the requesting Tenant Owner can cancel this request.");
        request.Cancel(); await unitOfWorkDbContext.SaveChangesAsync();
    }

    public async Task<PagedResultDto<TenantClosureRequestDto>> GetClosureHistoryAsync(Guid id, GetTenantClosureHistoryInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement(); var query = unitOfWorkDbContext.TenantClosureRequests.AsNoTracking().Where(x => x.TenantId == id);
        return new PagedResultDto<TenantClosureRequestDto> { TotalCount = await query.LongCountAsync(), Items = await query.OrderByDescending(x => x.CreationTime).Skip(Math.Max(0, input.SkipCount)).Take(Math.Clamp(input.MaxResultCount, 1, 100)).Select(x => new TenantClosureRequestDto { Id = x.Id, TenantId = x.TenantId, Status = x.Status, ReasonCode = x.ReasonCode, Note = x.Note, RequestedByUserId = x.RequestedByUserId, ApprovedByUserId = x.ApprovedByUserId, RejectedByUserId = x.RejectedByUserId, RejectedAtUtc = x.RejectedAtUtc, DecisionReasonCode = x.DecisionReasonCode, DecisionNote = x.DecisionNote, CreationTime = x.CreationTime, ClosedAtUtc = x.ClosedAtUtc, RestoredAtUtc = x.RestoredAtUtc }).ToListAsync() };
    }

    private static TenantClosureRequestDto MapClosureRequest(TenantClosureRequest x) => new() { Id = x.Id, TenantId = x.TenantId, Status = x.Status, ReasonCode = x.ReasonCode, Note = x.Note, RequestedByUserId = x.RequestedByUserId, ApprovedByUserId = x.ApprovedByUserId, RejectedByUserId = x.RejectedByUserId, RejectedAtUtc = x.RejectedAtUtc, DecisionReasonCode = x.DecisionReasonCode, DecisionNote = x.DecisionNote, CreationTime = x.CreationTime, ClosedAtUtc = x.ClosedAtUtc, RestoredAtUtc = x.RestoredAtUtc };


    [Audited]
    public async Task<TenantDto> UpdateAsync(Guid id, TenantUpdateDto input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: false);

        if (tenant == null)
        {
            throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        }

        var oldNormalizedName = tenant.NormalizedName;
        var wasActive = tenant.IsActive;
        var previousLifecycleStatus = tenant.GetEffectiveLifecycleStatus();
        await _tenantManager.ChangeNameAsync(tenant, input.Name);

        if (input.IsActive)
        {
            tenant.TransitionTo(TenantLifecycleStatus.Active);
        }
        else
        {
            tenant.TransitionTo(TenantLifecycleStatus.Suspended);
        }

        if (wasActive && !tenant.IsActive)
        {
            await _messages.PublishEventAsync(new TenantDeactivatedEto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                OccurredAtUtc = DateTime.UtcNow
            });
        }

        if (previousLifecycleStatus != tenant.GetEffectiveLifecycleStatus())
        {
            unitOfWorkDbContext.TenantLifecycleTransitions.Add(new TenantLifecycleTransition(
                Guid.CreateVersion7(),
                tenant.Id,
                previousLifecycleStatus,
                tenant.GetEffectiveLifecycleStatus(),
                "legacy-update",
                null));
            await _messages.PublishEventAsync(new TenantLifecycleChangedEto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                LifecycleRevision = tenant.LifecycleRevision,
                PreviousStatus = ToContract(previousLifecycleStatus),
                CurrentStatus = ToContract(tenant.GetEffectiveLifecycleStatus()),
                ReasonCode = "legacy-update",
                OccurredAtUtc = DateTime.UtcNow
            });
        }

        await _tenantRepository.UpdateAsync(tenant, autoSave: ShouldAutoSave());
        await ExecuteAfterCommitAsync(async () =>
        {
            if (!string.Equals(oldNormalizedName, tenant.NormalizedName, StringComparison.Ordinal))
            {
                await _tenantCacheManager.RemoveTenantAsync(id, oldNormalizedName);
            }

            await _tenantCacheManager.UpsertTenantAsync(tenant.Id);
        });
        return MapToDto(tenant);
    }

    [Audited(Category = "tenant.identity")]
    public async Task<TenantDto> RenameAsync(Guid id, TenantRenameInput input)
    {
        EnsureHostLifecycleManagement();
        ArgumentNullException.ThrowIfNull(input);
        var tenant = await FindTenantAsync(id, includeDetails: false, includeDeleted: false)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        if (string.IsNullOrWhiteSpace(input.ConcurrencyStamp))
        {
            throw new SharedValidationException("A concurrency stamp is required to rename a tenant.");
        }

        EnsureConcurrency(tenant, input.ConcurrencyStamp);
        _ = Shared.Check.NotNullOrWhiteSpace(input.Name, nameof(input.Name), maxLength: TenantConsts.MaxNameLength);
        var reasonCode = string.IsNullOrWhiteSpace(input.ReasonCode) ? "rename" : input.ReasonCode.Trim();
        if (reasonCode.Length > 64)
        {
            throw new SharedValidationException("Rename reason code must be 64 characters or fewer.");
        }

        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note?.Length > 500)
        {
            throw new SharedValidationException("Rename note must be 500 characters or fewer.");
        }

        var oldNormalizedName = tenant.NormalizedName;
        await _tenantManager.ChangeNameAsync(tenant, input.Name);
        await _tenantRepository.UpdateAsync(tenant, autoSave: ShouldAutoSave());
        await ExecuteAfterCommitAsync(async () =>
        {
            if (!string.Equals(oldNormalizedName, tenant.NormalizedName, StringComparison.Ordinal))
            {
                await _tenantCacheManager.RemoveTenantAsync(id, oldNormalizedName);
            }

            await _tenantCacheManager.UpsertTenantAsync(tenant.Id);
        });
        return MapToDto(tenant);
    }

    [Audited]
    public async Task<TenantDto> TransitionLifecycleAsync(
        Guid id,
        TenantLifecycleStatus targetStatus,
        TenantLifecycleTransitionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, includeDetails: false, includeDeleted: false)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        var reasonCode = Shared.Check.NotNullOrWhiteSpace(input.ReasonCode, nameof(input.ReasonCode), maxLength: 64);
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note?.Length > 500)
        {
            throw new SharedValidationException("Lifecycle transition note must be 500 characters or fewer.");
        }

        var previousStatus = tenant.GetEffectiveLifecycleStatus();
        tenant.TransitionTo(targetStatus);
        if (previousStatus == tenant.GetEffectiveLifecycleStatus())
        {
            return MapToDto(tenant);
        }

        unitOfWorkDbContext.TenantLifecycleTransitions.Add(new TenantLifecycleTransition(
            Guid.CreateVersion7(),
            tenant.Id,
            previousStatus,
            tenant.GetEffectiveLifecycleStatus(),
            reasonCode,
            note));

        if (previousStatus == TenantLifecycleStatus.Active && !tenant.IsActive)
        {
            await _messages.PublishEventAsync(new TenantDeactivatedEto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                OccurredAtUtc = DateTime.UtcNow
            });
        }

        await _messages.PublishEventAsync(new TenantLifecycleChangedEto
        {
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            LifecycleRevision = tenant.LifecycleRevision,
            PreviousStatus = ToContract(previousStatus),
            CurrentStatus = ToContract(tenant.GetEffectiveLifecycleStatus()),
            ReasonCode = reasonCode,
            OccurredAtUtc = DateTime.UtcNow
        });

        await _tenantRepository.UpdateAsync(tenant, autoSave: ShouldAutoSave());
        await ExecuteAfterCommitAsync(() => _tenantCacheManager.UpsertTenantAsync(tenant.Id));
        return MapToDto(tenant);
    }

    public async Task<PagedResultDto<TenantLifecycleTransitionDto>> GetLifecycleHistoryAsync(
        Guid id,
        GetTenantLifecycleHistoryInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        if (await FindTenantAsync(id, includeDetails: false, includeDeleted: true) == null)
        {
            throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        }

        var query = unitOfWorkDbContext.TenantLifecycleTransitions
            .AsNoTracking()
            .Where(transition => transition.TenantId == id);
        var totalCount = await query.LongCountAsync();
        var items = await query
            .OrderByDescending(transition => transition.CreationTime)
            .Skip(Math.Max(0, input.SkipCount))
            .Take(Math.Clamp(input.MaxResultCount, 1, 100))
            .Select(transition => new TenantLifecycleTransitionDto
            {
                Id = transition.Id,
                TenantId = transition.TenantId,
                PreviousStatus = transition.PreviousStatus,
                CurrentStatus = transition.CurrentStatus,
                ReasonCode = transition.ReasonCode,
                Note = transition.Note,
                OccurredAtUtc = transition.CreationTime,
                ActorUserId = transition.CreatorId
            })
            .ToListAsync();
        return new PagedResultDto<TenantLifecycleTransitionDto>
        {
            TotalCount = totalCount,
            Items = items
        };
    }

    public async Task<PagedResultDto<TenantHistoryEntryDto>> GetHistoryAsync(Guid id, GetTenantHistoryInput input)
    {
        // Host-only merged timeline over the three per-domain histories (P5).
        // Per-tenant cardinality is small, so we fetch (bounded) and merge/page in
        // memory — provider-agnostic and correct for TotalCount. Contact PII
        // (ownership target email) is intentionally not projected here.
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        if (await FindTenantAsync(id, includeDetails: false, includeDeleted: true) == null)
        {
            throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        }

        const int MergeCap = 5000;
        var entries = new List<TenantHistoryEntryDto>();

        if (input.EntryType is null or TenantHistoryEntryType.LifecycleTransition)
        {
            var rows = await unitOfWorkDbContext.TenantLifecycleTransitions.AsNoTracking()
                .Where(x => x.TenantId == id)
                .OrderByDescending(x => x.CreationTime)
                .Take(MergeCap)
                .ToListAsync();
            entries.AddRange(rows.Select(x => new TenantHistoryEntryDto
            {
                EntryType = TenantHistoryEntryType.LifecycleTransition,
                SourceId = x.Id,
                OccurredAtUtc = x.CreationTime,
                ActorUserId = x.CreatorId,
                Status = x.CurrentStatus.ToString(),
                PreviousStatus = x.PreviousStatus.ToString(),
                ReasonCode = x.ReasonCode,
                Note = x.Note
            }));
        }

        if (input.EntryType is null or TenantHistoryEntryType.OwnershipRequest)
        {
            var rows = await unitOfWorkDbContext.TenantOwnershipRequests.AsNoTracking()
                .Where(x => x.TenantId == id)
                .OrderByDescending(x => x.CreationTime)
                .Take(MergeCap)
                .ToListAsync();
            entries.AddRange(rows.Select(x => new TenantHistoryEntryDto
            {
                EntryType = TenantHistoryEntryType.OwnershipRequest,
                SourceId = x.Id,
                OccurredAtUtc = x.CreationTime,
                ActorUserId = x.TargetUserId ?? x.PreviousOwnerUserId,
                Status = x.Status.ToString(),
                Kind = x.Kind.ToString()
            }));
        }

        if (input.EntryType is null or TenantHistoryEntryType.ClosureRequest)
        {
            var rows = await unitOfWorkDbContext.TenantClosureRequests.AsNoTracking()
                .Where(x => x.TenantId == id)
                .OrderByDescending(x => x.CreationTime)
                .Take(MergeCap)
                .ToListAsync();
            entries.AddRange(rows.Select(x => new TenantHistoryEntryDto
            {
                EntryType = TenantHistoryEntryType.ClosureRequest,
                SourceId = x.Id,
                OccurredAtUtc = x.CreationTime,
                ActorUserId = x.ApprovedByUserId ?? x.RequestedByUserId,
                Status = x.Status.ToString(),
                ReasonCode = x.ReasonCode,
                Note = x.Note
            }));
        }

        var ordered = entries.OrderByDescending(x => x.OccurredAtUtc).ToList();
        var page = ordered
            .Skip(Math.Max(0, input.SkipCount))
            .Take(Math.Clamp(input.MaxResultCount, 1, 200))
            .ToList();

        return new PagedResultDto<TenantHistoryEntryDto>
        {
            TotalCount = ordered.Count,
            Items = page
        };
    }

    public async Task<TenantHistoryCursorPageDto> GetHistoryCursorAsync(Guid id, GetTenantHistoryCursorInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        input ??= new GetTenantHistoryCursorInput();
        if (await FindTenantAsync(id, includeDetails: false, includeDeleted: true) == null)
        {
            throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        }

        IQueryable<TenantHistoryQueryRow> query = unitOfWorkDbContext.TenantLifecycleTransitions.AsNoTracking()
            .Where(x => x.TenantId == id)
            .Select(x => new TenantHistoryQueryRow
            {
                EntryType = TenantHistoryEntryType.LifecycleTransition,
                SourceId = x.Id,
                OccurredAtUtc = x.CreationTime,
                ActorUserId = x.CreatorId,
                StatusCode = (int)x.CurrentStatus,
                PreviousStatusCode = (int?)x.PreviousStatus,
                KindCode = null,
                ReasonCode = x.ReasonCode,
                Note = x.Note
            });

        if (input.EntryType is null or TenantHistoryEntryType.OwnershipRequest)
        {
            var ownership = unitOfWorkDbContext.TenantOwnershipRequests.AsNoTracking()
                .Where(x => x.TenantId == id)
                .Select(x => new TenantHistoryQueryRow
                {
                    EntryType = TenantHistoryEntryType.OwnershipRequest,
                    SourceId = x.Id,
                    OccurredAtUtc = x.CreationTime,
                    ActorUserId = x.TargetUserId ?? x.PreviousOwnerUserId,
                    StatusCode = (int)x.Status,
                    PreviousStatusCode = null,
                    KindCode = (int?)x.Kind,
                    ReasonCode = null,
                    Note = null
                });
            query = input.EntryType == TenantHistoryEntryType.OwnershipRequest ? ownership : query.Concat(ownership);
        }

        if (input.EntryType is null or TenantHistoryEntryType.ClosureRequest)
        {
            var closure = unitOfWorkDbContext.TenantClosureRequests.AsNoTracking()
                .Where(x => x.TenantId == id)
                .Select(x => new TenantHistoryQueryRow
                {
                    EntryType = TenantHistoryEntryType.ClosureRequest,
                    SourceId = x.Id,
                    OccurredAtUtc = x.CreationTime,
                    ActorUserId = x.ApprovedByUserId ?? x.RequestedByUserId,
                    StatusCode = (int)x.Status,
                    PreviousStatusCode = null,
                    KindCode = null,
                    ReasonCode = x.ReasonCode,
                    Note = x.Note
                });
            query = input.EntryType == TenantHistoryEntryType.ClosureRequest ? closure : query.Concat(closure);
        }

        if (input.EntryType == TenantHistoryEntryType.LifecycleTransition)
        {
            query = query.Where(x => x.EntryType == TenantHistoryEntryType.LifecycleTransition);
        }

        var cursor = ParseHistoryCursor(input.Cursor);
        if (cursor != null)
        {
            query = query.Where(x => x.OccurredAtUtc < cursor.OccurredAtUtc ||
                                     (x.OccurredAtUtc == cursor.OccurredAtUtc && x.SourceId.CompareTo(cursor.SourceId) < 0));
        }

        var pageSize = Math.Clamp(input.MaxResultCount, 1, 200);
        var rows = await query
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.SourceId)
            .Take(pageSize + 1)
            .ToListAsync();
        var hasMore = rows.Count > pageSize;
        var pageRows = rows.Take(pageSize).ToList();
        var last = pageRows.LastOrDefault();

        return new TenantHistoryCursorPageDto
        {
            Items = pageRows.Select(MapHistoryCursorRow).ToList(),
            HasMore = hasMore,
            NextCursor = hasMore && last != null ? CreateHistoryCursor(last) : null
        };
    }

    public async Task<TenantOperationalSummaryDto> GetOperationalSummaryAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: true)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);

        var activeOwnership = await unitOfWorkDbContext.TenantOwnershipRequests.AsNoTracking()
            .CountAsync(x => x.TenantId == id &&
                (x.Status == TenantOwnershipRequestStatus.PendingInvitation ||
                 x.Status == TenantOwnershipRequestStatus.AwaitingEmailVerification));
        var activeClosure = await unitOfWorkDbContext.TenantClosureRequests.AsNoTracking()
            .CountAsync(x => x.TenantId == id &&
                (x.Status == TenantClosureStatus.Requested || x.Status == TenantClosureStatus.Approved));
        var lastTransitionAt = await unitOfWorkDbContext.TenantLifecycleTransitions.AsNoTracking()
            .Where(x => x.TenantId == id)
            .OrderByDescending(x => x.CreationTime)
            .Select(x => (DateTime?)x.CreationTime)
            .FirstOrDefaultAsync();
        var latestClosure = await unitOfWorkDbContext.TenantClosureRequests.AsNoTracking()
            .Where(x => x.TenantId == id)
            .OrderByDescending(x => x.CreationTime)
            .ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Status })
            .FirstOrDefaultAsync();
        var canArchive = !tenant.IsDeleted && latestClosure?.Status == TenantClosureStatus.Closed;

        return new TenantOperationalSummaryDto
        {
            TenantId = tenant.Id,
            IsActive = tenant.IsActive,
            IsDeleted = tenant.IsDeleted,
            LifecycleStatus = tenant.GetEffectiveLifecycleStatus(),
            OwnerUserId = tenant.OwnerUserId,
            DedicatedDatabaseCount = TenantDatabaseStatusMapper.CountDedicated(tenant),
            ActiveOwnershipRequestCount = activeOwnership,
            ActiveClosureRequestCount = activeClosure,
            LastLifecycleTransitionAtUtc = lastTransitionAt,
            CreationTime = tenant.CreationTime,
            LastModificationTime = tenant.LastModificationTime,
            AllowedLifecycleTransitions = tenant.IsDeleted ? [] : tenant.GetAllowedTransitions(),
            CanArchive = canArchive,
            ArchiveBlockedReason = canArchive
                ? null
                : tenant.IsDeleted
                    ? "Tenant is already archived."
                    : "Closure is not complete.",
            LatestClosureRequestId = latestClosure?.Id,
            LatestClosureStatus = latestClosure?.Status,
            CanRestore = tenant.IsDeleted
        };
    }

    public async Task<TenantActionImpactDto> GetActionImpactAsync(Guid id, TenantActionImpactAction action)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: true)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        var activeOwnership = await unitOfWorkDbContext.TenantOwnershipRequests.AsNoTracking().CountAsync(x =>
            x.TenantId == id &&
            (x.Status == TenantOwnershipRequestStatus.PendingInvitation || x.Status == TenantOwnershipRequestStatus.AwaitingEmailVerification));
        var activeClosure = await unitOfWorkDbContext.TenantClosureRequests.AsNoTracking().CountAsync(x =>
            x.TenantId == id &&
            (x.Status == TenantClosureStatus.Requested || x.Status == TenantClosureStatus.Approved));
        var latestClosure = await unitOfWorkDbContext.TenantClosureRequests.AsNoTracking()
            .Where(x => x.TenantId == id)
            .OrderByDescending(x => x.CreationTime)
            .ThenByDescending(x => x.Id)
            .Select(x => new { x.Status })
            .FirstOrDefaultAsync();
        var blockers = new List<string>();
        var currentStatus = tenant.GetEffectiveLifecycleStatus();
        var targetStatus = action switch
        {
            TenantActionImpactAction.Suspend => TenantLifecycleStatus.Suspended,
            TenantActionImpactAction.Close => TenantLifecycleStatus.Suspended,
            TenantActionImpactAction.Archive => TenantLifecycleStatus.Decommissioned,
            TenantActionImpactAction.Activate => TenantLifecycleStatus.Active,
            _ => throw new SharedValidationException("Unsupported tenant impact action.")
        };
        if (tenant.IsDeleted)
        {
            blockers.Add("Tenant is already archived.");
        }
        if (action == TenantActionImpactAction.Archive && latestClosure?.Status != TenantClosureStatus.Closed)
        {
            blockers.Add("Closure is not complete.");
        }

        var enrichment = _impactEnrichmentClient == null
            ? new TenantImpactEnrichmentResult(
                TenantImpactDependencyCount.Unavailable,
                TenantImpactDependencyCount.Unavailable)
            : await _impactEnrichmentClient.GetAsync(id);

        return new TenantActionImpactDto
        {
            TenantId = id,
            Action = action,
            CurrentStatus = currentStatus,
            TargetStatus = targetStatus,
            HasOwner = tenant.OwnerUserId.HasValue,
            DedicatedDatabaseCount = TenantDatabaseStatusMapper.CountDedicated(tenant),
            ActiveOwnershipRequestCount = activeOwnership,
            ActiveClosureRequestCount = activeClosure,
            Preconditions = action == TenantActionImpactAction.Archive
                ? ["The latest closure request must be Closed."]
                : ["The action requires a host management permission."],
            Blockers = blockers,
            Consequences = action switch
            {
                TenantActionImpactAction.Suspend => ["Tenant users can no longer use the tenant until it is reactivated."],
                TenantActionImpactAction.Close => ["Closing the tenant deactivates access and records the closure decision."],
                TenantActionImpactAction.Archive => ["Archiving soft-deletes the tenant and requires a later restore before activation."],
                TenantActionImpactAction.Activate => ["Tenant users regain access after activation."],
                _ => []
            },
            ActiveSessionCount = enrichment.Identity.Count,
            IdentityStatus = enrichment.Identity.IsAvailable
                ? TenantImpactDependencyStatus.Ok
                : TenantImpactDependencyStatus.Unavailable,
            PendingNotificationCount = enrichment.Notifications.Count,
            NotificationsStatus = enrichment.Notifications.IsAvailable
                ? TenantImpactDependencyStatus.Ok
                : TenantImpactDependencyStatus.Unavailable
        };
    }


    [Audited]
    public async Task DeleteAsync(Guid id)
    {
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: true);
        if (tenant == null || tenant.IsDeleted) return;
        await ArchiveCoreAsync(
            tenant,
            new TenantArchiveInput
            {
                TenantNameConfirmation = tenant.Name,
                ReasonCode = "legacy-closed-archive",
                Note = "Archived through the compatibility DELETE route."
            },
            allowOverride: false);
    }

    [Audited(Category = "tenant.lifecycle")]
    public async Task<TenantArchiveResultDto> ArchiveAsync(Guid id, TenantArchiveInput input)
    {
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: true)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        return await ArchiveCoreAsync(tenant, input, allowOverride: false);
    }

    [Audited(Category = "tenant.lifecycle")]
    public async Task<TenantArchiveResultDto> ArchiveOverrideAsync(Guid id, TenantArchiveInput input)
    {
        EnsureHostLifecycleManagement();
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: true)
            ?? throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        return await ArchiveCoreAsync(tenant, input, allowOverride: true);
    }

    private async Task<TenantArchiveResultDto> ArchiveCoreAsync(
        Domain.Tenants.Tenant tenant,
        TenantArchiveInput input,
        bool allowOverride)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input ??= new TenantArchiveInput();
        var previousStatus = tenant.GetEffectiveLifecycleStatus();
        if (tenant.IsDeleted)
        {
            return new TenantArchiveResultDto
            {
                TenantId = tenant.Id,
                PreviousStatus = previousStatus,
                CurrentStatus = previousStatus,
                UsedOverride = allowOverride,
                WasAlreadyArchived = true,
                ArchivedAtUtc = tenant.DeletionTime
            };
        }

        if (!string.Equals(
                input.TenantNameConfirmation?.Trim(),
                tenant.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SharedValidationException("Tenant name confirmation does not match.");
        }

        var reason = Shared.Check.NotNullOrWhiteSpace(
            input.ReasonCode,
            nameof(input.ReasonCode),
            maxLength: 64).Trim();
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note?.Length > 500)
        {
            throw new SharedValidationException("Archive note must be 500 characters or fewer.");
        }

        var latestClosure = await unitOfWorkDbContext.TenantClosureRequests.AsNoTracking()
            .Where(x => x.TenantId == tenant.Id)
            .OrderByDescending(x => x.CreationTime)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync();
        if (!allowOverride && latestClosure?.Status != TenantClosureStatus.Closed)
        {
            throw new SharedValidationException("Normal archive requires the latest closure request to be Closed.");
        }

        var now = DateTime.UtcNow;
        var oldNormalizedName = tenant.NormalizedName;
        var wasActive = tenant.IsActive;
        tenant.TransitionTo(TenantLifecycleStatus.Decommissioned);
        var currentStatus = tenant.GetEffectiveLifecycleStatus();
        unitOfWorkDbContext.TenantLifecycleTransitions.Add(new TenantLifecycleTransition(
            Guid.CreateVersion7(), tenant.Id, previousStatus, currentStatus, reason, note));
        tenant.IsDeleted = true;
        tenant.DeletionTime = now;

        if (wasActive)
        {
            await _messages.PublishEventAsync(new TenantDeactivatedEto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                OccurredAtUtc = now
            });
        }

        await _messages.PublishEventAsync(new TenantLifecycleChangedEto
        {
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            LifecycleRevision = tenant.LifecycleRevision,
            PreviousStatus = ToContract(previousStatus),
            CurrentStatus = ToContract(currentStatus),
            ReasonCode = reason,
            OccurredAtUtc = now
        });

        await _tenantRepository.UpdateAsync(tenant, autoSave: ShouldAutoSave());
        await ExecuteAfterCommitAsync(() => _tenantCacheManager.TombstoneTenantAsync(tenant.Id, oldNormalizedName));

        return new TenantArchiveResultDto
        {
            TenantId = tenant.Id,
            ClosureRequestId = latestClosure?.Id,
            PreviousStatus = previousStatus,
            CurrentStatus = currentStatus,
            UsedOverride = allowOverride,
            ArchivedAtUtc = now
        };
    }

    [Audited(Category = "tenant.lifecycle")]
    public async Task RestoreAsync(Guid id, TenantRestoreInput input)
    {
        _ = await RestoreCoreAsync(id, input, requireConcurrency: false);
    }

    [Audited(Category = "tenant.lifecycle")]
    public Task<TenantRestoreResultDto> RestoreDetailedAsync(Guid id, TenantRestoreInput input)
    {
        return RestoreCoreAsync(id, input, requireConcurrency: true);
    }

    private async Task<TenantRestoreResultDto> RestoreCoreAsync(
        Guid id,
        TenantRestoreInput input,
        bool requireConcurrency)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostLifecycleManagement();
        input ??= new TenantRestoreInput();
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: true);
        if (tenant == null)
        {
            throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), id);
        }

        if (!tenant.IsDeleted)
        {
            var status = tenant.GetEffectiveLifecycleStatus();
            return new TenantRestoreResultDto
            {
                TenantId = tenant.Id,
                PreviousStatus = status,
                CurrentStatus = status,
                WasAlreadyRestored = true,
                AllowedNextActions = GetAllowedNextActions(tenant)
            };
        }

        if (requireConcurrency && string.IsNullOrWhiteSpace(input.ConcurrencyStamp))
        {
            throw new SharedValidationException("A concurrency stamp is required to restore a tenant.");
        }

        EnsureConcurrency(tenant, input.ConcurrencyStamp);

        // Block restore if an active (non-deleted) tenant now holds this name.
        // The check runs with the soft-delete filter enabled, so only active
        // tenants count and the still-deleted tenant being restored is excluded.
        if (await _tenantManager.IsNameTakenAsync(tenant.NormalizedName))
        {
            throw new TenantNameAlreadyExistsException(tenant.Name);
        }

        var reasonCode = Shared.Check.NotNullOrWhiteSpace(
            string.IsNullOrWhiteSpace(input?.ReasonCode) ? "restore" : input!.ReasonCode,
            nameof(TenantRestoreInput.ReasonCode), maxLength: 64).Trim();
        var note = string.IsNullOrWhiteSpace(input?.Note) ? null : input!.Note.Trim();
        if (note?.Length > 500)
        {
            throw new SharedValidationException("Restore note must be 500 characters or fewer.");
        }

        var previousStatus = tenant.GetEffectiveLifecycleStatus();
        tenant.RestoreAsSuspended();
        // Restore deactivated so reactivation is a conscious, separate step
        // (fail-closed): a restored tenant cannot resolve to any database until
        // an admin explicitly reactivates it.
        var currentStatus = tenant.GetEffectiveLifecycleStatus();
        var restoredAtUtc = DateTime.UtcNow;

        // Record a lifecycle-history entry with the caller's reason so restore is
        // audit-friendly (P1). A dedicated TenantRestoredEto is deferred to P4;
        // publish the existing lifecycle event only when the status actually changed.
        unitOfWorkDbContext.TenantLifecycleTransitions.Add(new TenantLifecycleTransition(
            Guid.CreateVersion7(), tenant.Id, previousStatus, currentStatus, reasonCode, note));

        if (previousStatus != currentStatus)
        {
            await _messages.PublishEventAsync(new TenantLifecycleChangedEto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                LifecycleRevision = tenant.LifecycleRevision,
                PreviousStatus = ToContract(previousStatus),
                CurrentStatus = ToContract(currentStatus),
                ReasonCode = reasonCode,
                OccurredAtUtc = restoredAtUtc
            });
        }

        await _tenantRepository.UpdateAsync(tenant, autoSave: ShouldAutoSave());
        await ExecuteAfterCommitAsync(() => _tenantCacheManager.UpsertTenantAsync(tenant.Id));
        return new TenantRestoreResultDto
        {
            TenantId = tenant.Id,
            PreviousStatus = previousStatus,
            CurrentStatus = currentStatus,
            RestoredAtUtc = restoredAtUtc,
            AllowedNextActions = GetAllowedNextActions(tenant)
        };
    }

    [Audited]
    public async Task DeleteProvisioningAsync(Guid id)
    {
        var tenant = await FindTenantAsync(id, includeDetails: true, includeDeleted: true);
        if (tenant == null)
        {
            return;
        }

        var oldNormalizedName = tenant.NormalizedName;
        await _tenantRepository.DeleteAsync(tenant, autoSave: ShouldAutoSave());
        await ExecuteAfterCommitAsync(
            () => _tenantCacheManager.RemoveTenantAsync(id, oldNormalizedName));
    }

    private void EnsureHostLifecycleManagement()
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Tenant lifecycle management is restricted to host administrators.");
        }
    }

    private bool ShouldAutoSave()
    {
        return _unitOfWorkManager.Current == null;
    }

    private async Task<Domain.Tenants.Tenant?> FindTenantAsync(
        Guid id,
        bool includeDetails,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        if (includeDeleted)
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                return await QueryTenantAsync(id, includeDetails, cancellationToken);
            }
        }

        return await QueryTenantAsync(id, includeDetails, cancellationToken);
    }

    private async Task<Domain.Tenants.Tenant?> QueryTenantAsync(
        Guid id,
        bool includeDetails,
        CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        IQueryable<Domain.Tenants.Tenant> query = unitOfWorkDbContext.Tenants;

        if (includeDetails)
        {
            query = query.Include(x => x.ConnectionStrings);
        }

        return await query.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    private Task ExecuteAfterCommitAsync(Func<Task> action)
    {
        var unitOfWork = _unitOfWorkManager.Current;
        if (unitOfWork == null)
        {
            return action();
        }

        unitOfWork.OnCompleted(action);
        return Task.CompletedTask;
    }

    private Task ScheduleTenantCacheUpsertAfterCommitAsync(Domain.Tenants.Tenant tenant)
    {
        // Completion handlers run after the transaction-owned DbContext has
        // committed and may no longer query through that context. Cache the
        // already-materialized aggregate instead of resolving it again by ID.
        return ExecuteAfterCommitAsync(() => _tenantCacheManager.UpsertTenantAsync(tenant));
    }

    private static TenantDto MapToDto(Domain.Tenants.Tenant tenant)
    {
        return new TenantDto
        {
            Id = tenant.Id,
            Name = tenant.Name,
            IsActive = tenant.IsActive,
            IsDeleted = tenant.IsDeleted,
            LifecycleStatus = tenant.GetEffectiveLifecycleStatus(),
            CreationTime = tenant.CreationTime,
            LastModificationTime = tenant.LastModificationTime
        };
    }

    private static TenantDetailDto MapToDetailDto(Domain.Tenants.Tenant tenant)
    {
        return new TenantDetailDto
        {
            Id = tenant.Id,
            Name = tenant.Name,
            IsActive = tenant.IsActive,
            IsDeleted = tenant.IsDeleted,
            LifecycleStatus = tenant.GetEffectiveLifecycleStatus(),
            OwnerUserId = tenant.OwnerUserId,
            CreationTime = tenant.CreationTime,
            LastModificationTime = tenant.LastModificationTime,
            DeletionTime = tenant.DeletionTime,
            ConcurrencyStamp = tenant.ConcurrencyStamp,
            Databases = TenantDatabaseStatusMapper.Map(tenant)
        };
    }

    private static TenantLifecycleStatusEto ToContract(TenantLifecycleStatus status)
    {
        return status switch
        {
            TenantLifecycleStatus.Active => TenantLifecycleStatusEto.Active,
            TenantLifecycleStatus.Provisioning => TenantLifecycleStatusEto.Provisioning,
            TenantLifecycleStatus.Suspended => TenantLifecycleStatusEto.Suspended,
            TenantLifecycleStatus.ProvisioningFailed => TenantLifecycleStatusEto.ProvisioningFailed,
            TenantLifecycleStatus.Decommissioned => TenantLifecycleStatusEto.Decommissioned,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported tenant lifecycle status.")
        };
    }

    private static TenantOwnershipRequestKindEto ToContract(TenantOwnershipRequestKind kind)
    {
        return kind switch
        {
            TenantOwnershipRequestKind.InitialOwner => TenantOwnershipRequestKindEto.InitialOwner,
            TenantOwnershipRequestKind.Transfer => TenantOwnershipRequestKindEto.Transfer,
            TenantOwnershipRequestKind.Recovery => TenantOwnershipRequestKindEto.Recovery,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported tenant ownership request kind.")
        };
    }

    private static void EnsureConcurrency(Domain.Tenants.Tenant tenant, string? expectedStamp)
    {
        if (!string.IsNullOrWhiteSpace(expectedStamp) &&
            !string.Equals(tenant.ConcurrencyStamp, expectedStamp, StringComparison.Ordinal))
        {
            throw new SharedValidationException("The tenant was modified by someone else. Reload and try again.");
        }
    }

    private static string[] GetAllowedNextActions(Domain.Tenants.Tenant tenant)
    {
        return tenant.IsDeleted
            ? []
            : tenant.GetAllowedTransitions()
                .Select(status => status switch
                {
                    TenantLifecycleStatus.Active => "activate",
                    TenantLifecycleStatus.Suspended => "suspend",
                    TenantLifecycleStatus.Provisioning => "resume-provisioning",
                    TenantLifecycleStatus.ProvisioningFailed => "review-provisioning-failure",
                    TenantLifecycleStatus.Decommissioned => "decommission",
                    _ => status.ToString()
                })
                .ToArray();
    }

    private static TenantHistoryEntryDto MapHistoryCursorRow(TenantHistoryQueryRow row)
    {
        return new TenantHistoryEntryDto
        {
            EntryType = row.EntryType,
            SourceId = row.SourceId,
            OccurredAtUtc = row.OccurredAtUtc,
            ActorUserId = row.ActorUserId,
            Status = row.StatusCode is null ? null : row.EntryType switch
            {
                TenantHistoryEntryType.LifecycleTransition => ((TenantLifecycleStatus)row.StatusCode.Value).ToString(),
                TenantHistoryEntryType.OwnershipRequest => ((TenantOwnershipRequestStatus)row.StatusCode.Value).ToString(),
                TenantHistoryEntryType.ClosureRequest => ((TenantClosureStatus)row.StatusCode.Value).ToString(),
                _ => null
            },
            PreviousStatus = row.PreviousStatusCode is null ? null : ((TenantLifecycleStatus)row.PreviousStatusCode.Value).ToString(),
            Kind = row.KindCode is null ? null : ((TenantOwnershipRequestKind)row.KindCode.Value).ToString(),
            ReasonCode = row.ReasonCode,
            Note = row.Note
        };
    }

    private static string CreateHistoryCursor(TenantHistoryQueryRow row)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new TenantHistoryCursor(row.OccurredAtUtc, row.SourceId));
        return Convert.ToBase64String(payload);
    }

    private static TenantHistoryCursor? ParseHistoryCursor(string? encodedCursor)
    {
        if (string.IsNullOrWhiteSpace(encodedCursor))
        {
            return null;
        }

        try
        {
            var cursor = JsonSerializer.Deserialize<TenantHistoryCursor>(Convert.FromBase64String(encodedCursor));
            if (cursor is null || cursor.SourceId == Guid.Empty)
            {
                throw new JsonException();
            }

            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new SharedValidationException("The history cursor is invalid.");
        }
    }

    private sealed class TenantHistoryQueryRow
    {
        public TenantHistoryEntryType EntryType { get; init; }
        public Guid SourceId { get; init; }
        public DateTime OccurredAtUtc { get; init; }
        public Guid? ActorUserId { get; init; }
        public int? StatusCode { get; init; }
        public int? PreviousStatusCode { get; init; }
        public int? KindCode { get; init; }
        public string? ReasonCode { get; init; }
        public string? Note { get; init; }
    }

    private sealed record TenantHistoryCursor(DateTime OccurredAtUtc, Guid SourceId);
}
