using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Identity.Application.Account;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Invitations;
using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Shared.EventContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Shared.CurrentUser;
using Shared.EventBus.Messaging;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Invitations;

public interface IOwnershipInvitationProvisioner
{
    Task CreateOwnershipInvitationAsync(
        TenantOwnershipInvitationRequestedEto input,
        CancellationToken cancellationToken = default);

    Task CancelOwnershipInvitationAsync(
        TenantOwnershipInvitationCanceledEto input,
        CancellationToken cancellationToken = default);
}

[ExposeServices(typeof(IInvitationAppService), typeof(IOwnershipInvitationProvisioner))]
public sealed class InvitationAppService :
    IInvitationAppService,
    IOwnershipInvitationProvisioner,
    ITransientDependency
{
    private const string AcceptInvitationPublicPath = "/auth/accept-invite";
    private const string PlatformStaffInvitationName = "TenantWright platform";
    private static readonly Guid PlatformStaffTenantId = Guid.Empty;

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ApplicationRoleManager _roleManager;
    private readonly TenantUserManagementGuard _managementGuard;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;
    private readonly IOptions<PublicInvitationManagementOptions> _options;
    private readonly ITenantWrightMessageSender _eventBus;

    public InvitationAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ApplicationRoleManager roleManager, TenantUserManagementGuard managementGuard, ICurrentUser currentUser, ICurrentTenant currentTenant, IOptions<PublicInvitationManagementOptions> options, ITenantWrightMessageSender eventBus)
    {
        _dbContextProvider = dbContextProvider;
        _roleManager = roleManager;
        _managementGuard = managementGuard;
        _currentUser = currentUser;
        _currentTenant = currentTenant;
        _options = options;
        _eventBus = eventBus;
    }

    public async Task<PagedResultDto<InvitationDto>> GetListAsync(InvitationListInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        input ??= new InvitationListInput();

        var tenantId = _managementGuard.ResolveTenantId(input.TenantId);
        using var tenantScope = _currentTenant.Change(tenantId, _currentTenant.Name);
        var now = DateTime.UtcNow;
        var query = unitOfWorkDbContext.IdentityPublicInvitations.Where(x => x.TenantId == tenantId);

        var email = input.Email?.Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            query = query.Where(x => x.Email.Contains(email));
        }

        var status = NormalizeInvitationStatus(input.Status);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = status switch
            {
                "accepted" => query.Where(x => x.AcceptedAtUtc.HasValue),
                "canceled" => query.Where(x => x.CanceledAtUtc.HasValue),
                "expired" => query.Where(x => !x.AcceptedAtUtc.HasValue && !x.CanceledAtUtc.HasValue && x.ExpiresAtUtc <= now),
                _ => query.Where(x => !x.AcceptedAtUtc.HasValue && !x.CanceledAtUtc.HasValue && x.ExpiresAtUtc > now)
            };
        }

        if (input.ExpiresWithinDays.HasValue)
        {
            var horizon = now.AddDays(Math.Max(0, input.ExpiresWithinDays.Value));
            query = query.Where(x => x.ExpiresAtUtc <= horizon);
        }

        if (!_managementGuard.IsCurrentUserHostAdmin())
        {
            query = query.Where(x =>
                string.IsNullOrWhiteSpace(x.RoleName) ||
                !string.Equals(TenantWrightIdentityRoleNames.Normalize(x.RoleName), TenantWrightIdentityRoleNames.TenantAdmin, StringComparison.Ordinal));
        }

        // Rejects an undeclared field instead of silently falling back to the default order.
        query = ApplySorting(query, SortingValidator.ValidateAndNormalize<InvitationListInput>(input.Sorting));
        var totalCount = await query.LongCountAsync();
        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var items = await query.Skip(skipCount).Take(maxResultCount).ToListAsync();

        return new PagedResultDto<InvitationDto>
        {
            TotalCount = totalCount,
            Items = items.Select(MapToDto).ToList()
        };
    }

    public async Task<InvitationDto> CreateAsync(CreateInvitationInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.ResolveTenantId(input.TenantId);
        using var tenantScope = _currentTenant.Change(tenantId, _currentTenant.Name);
        var safeBaseUrl = ResolveAllowedBaseUrl(input.AcceptanceBaseUrl);
        var roleName = _managementGuard.NormalizeSingleRole(input.RoleName);
        _managementGuard.EnsureCanAssignRoles([roleName]);
        await EnsureRoleExistsAsync(tenantId, roleName);

        var (invitation, rawToken) = await CreateInvitationEntityAsync(
            tenantId,
            NormalizeRequiredInput(input.Email, 256),
            roleName,
            NormalizeOptionalInput(input.SuggestedUserName, 256),
            cancelSupersededForEmail: true);

        await QueueInvitationNotificationAsync(invitation, rawToken, safeBaseUrl);
        await unitOfWorkDbContext.SaveChangesAsync();

        return MapToDto(invitation);
    }

    public async Task<InvitationBatchValidationDto> ValidateBatchAsync(ValidateInvitationBatchInput input)
    {
        _managementGuard.EnsureUserManagementActor();
        input ??= new ValidateInvitationBatchInput();
        var tenantId = _managementGuard.ResolveTenantId(input.TenantId);
        using var tenantScope = _currentTenant.Change(tenantId, _currentTenant.Name);
        EnsureBatchWithinLimits(input.Items);

        var (valid, errors) = await BuildBatchPlanAsync(tenantId, input.Items);

        return new InvitationBatchValidationDto
        {
            TotalRowCount = input.Items.Count,
            ValidRowCount = valid.Count,
            Errors = errors
        };
    }

    public async Task<InvitationBatchResultDto> CreateBatchAsync(CreateInvitationBatchInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        input ??= new CreateInvitationBatchInput();
        var tenantId = _managementGuard.ResolveTenantId(input.TenantId);
        using var tenantScope = _currentTenant.Change(tenantId, _currentTenant.Name);
        var safeBaseUrl = ResolveAllowedBaseUrl(input.AcceptanceBaseUrl);
        EnsureBatchWithinLimits(input.Items);

        var (valid, errors) = await BuildBatchPlanAsync(tenantId, input.Items);

        var created = new List<InvitationDto>(valid.Count);
        foreach (var row in valid)
        {
            // Auto-create the tenant-admin role at commit time when applicable,
            // mirroring single-invite behavior; other roles were confirmed to
            // exist during planning.
            await EnsureRoleExistsAsync(tenantId, row.RoleName);
            var (invitation, rawToken) = await CreateInvitationEntityAsync(
                tenantId,
                row.Email,
                row.RoleName,
                row.SuggestedUserName,
                cancelSupersededForEmail: false);
            await QueueInvitationNotificationAsync(invitation, rawToken, safeBaseUrl);
            created.Add(MapToDto(invitation));
        }

        if (created.Count > 0)
        {
            await unitOfWorkDbContext.SaveChangesAsync();
        }

        return new InvitationBatchResultDto
        {
            RequestedCount = input.Items.Count,
            CreatedCount = created.Count,
            SkippedCount = input.Items.Count - created.Count,
            Created = created,
            Errors = errors
        };
    }

    private void EnsureBatchWithinLimits(List<InvitationImportItem> items)
    {
        if (items == null || items.Count == 0)
        {
            throw new SharedValidationException("At least one invitation row is required.");
        }

        var maxBatchSize = Math.Clamp(_options.Value.MaxBatchSize, 1, 1000);
        if (items.Count > maxBatchSize)
        {
            throw new SharedValidationException($"A bulk invitation request cannot exceed {maxBatchSize} rows.");
        }
    }

    // Read-only planning shared by validate and create: normalizes and validates
    // every row, records one error per rejected row, and returns the rows that
    // are safe to create. No entity is written here.
    private async Task<(List<PlannedInvitation> Valid, List<InvitationRowErrorDto> Errors)> BuildBatchPlanAsync(
        Guid tenantId,
        List<InvitationImportItem> items)
    {
        var now = DateTime.UtcNow;
        var errors = new List<InvitationRowErrorDto>();
        var valid = new List<PlannedInvitation>();
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (existingPendingEmails, existingUserEmails) = await LoadExistingEmailsAsync(tenantId, items, now);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index] ?? new InvitationImportItem();
            var rowNumber = item.RowNumber > 0 ? item.RowNumber : index + 1;

            var email = item.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email))
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.Email), "Email is required."));
                continue;
            }

            if (email.Length > 256)
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.Email), "Email must be 256 characters or fewer."));
                continue;
            }

            if (!IsValidEmail(email))
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.Email), "Email is not a valid address."));
                continue;
            }

            var roleName = item.RoleName?.Trim();
            if (string.IsNullOrWhiteSpace(roleName))
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.RoleName), "Role is required."));
                continue;
            }

            if (roleName.Length > 64)
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.RoleName), "Role must be 64 characters or fewer."));
                continue;
            }

            var normalizedRole = TenantWrightIdentityRoleNames.Normalize(roleName);

            try
            {
                _managementGuard.EnsureCanAssignRoles([normalizedRole]);
            }
            catch (UnauthorizedAccessException ex)
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.RoleName), ex.Message));
                continue;
            }

            if (!await RoleExistsOrCreatableAsync(tenantId, normalizedRole))
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.RoleName), $"Role '{roleName}' does not exist for this tenant."));
                continue;
            }

            string? suggestedUserName = item.SuggestedUserName?.Trim();
            if (!string.IsNullOrWhiteSpace(suggestedUserName) && suggestedUserName.Length > 256)
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.SuggestedUserName), "Suggested user name must be 256 characters or fewer."));
                continue;
            }

            suggestedUserName = string.IsNullOrWhiteSpace(suggestedUserName) ? null : suggestedUserName;

            if (!seenEmails.Add(email))
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.Email), "Duplicate email in this batch."));
                continue;
            }

            if (existingPendingEmails.Contains(email))
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.Email), "An active invitation already exists for this email."));
                continue;
            }

            if (existingUserEmails.Contains(email))
            {
                errors.Add(RowError(rowNumber, nameof(InvitationImportItem.Email), "A user with this email already exists in this tenant."));
                continue;
            }

            valid.Add(new PlannedInvitation(rowNumber, email, normalizedRole, suggestedUserName));
        }

        return (valid, errors);
    }

    private async Task<(HashSet<string> PendingEmails, HashSet<string> UserEmails)> LoadExistingEmailsAsync(
        Guid tenantId,
        List<InvitationImportItem> items,
        DateTime now)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var candidateEmails = items
                    .Select(x => x?.Email?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.ToUpperInvariant())
                    .Distinct()
                    .ToList();

        var pendingEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (candidateEmails.Count == 0)
        {
            return (pendingEmails, userEmails);
        }

        var pending = await unitOfWorkDbContext.IdentityPublicInvitations
            .Where(x =>
                x.TenantId == tenantId &&
                !x.AcceptedAtUtc.HasValue &&
                !x.CanceledAtUtc.HasValue &&
                x.ExpiresAtUtc > now)
            .Select(x => x.Email)
            .ToListAsync();
        foreach (var email in pending)
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                pendingEmails.Add(email.Trim());
            }
        }

        var users = await unitOfWorkDbContext.Users
            .Where(x => x.TenantId == tenantId && x.NormalizedEmail != null && candidateEmails.Contains(x.NormalizedEmail))
            .Select(x => x.Email)
            .ToListAsync();
        foreach (var email in users)
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                userEmails.Add(email.Trim());
            }
        }

        return (pendingEmails, userEmails);
    }

    private async Task<bool> RoleExistsOrCreatableAsync(Guid tenantId, string normalizedRole)
    {
        var upper = normalizedRole.ToUpperInvariant();
        if (await _roleManager.Roles.AnyAsync(x => x.TenantId == tenantId && x.NormalizedName == upper))
        {
            return true;
        }

        // The tenant-admin role is auto-provisioned at commit time, matching
        // single-invite behavior, so treat it as creatable during planning.
        return TenantWrightIdentityRoleNames.IsTenantAdminRole(normalizedRole);
    }

    private static InvitationRowErrorDto RowError(int rowNumber, string field, string message)
        => new() { RowNumber = rowNumber, Field = field, Message = message };

    private static readonly System.ComponentModel.DataAnnotations.EmailAddressAttribute EmailAddressValidator = new();

    private static bool IsValidEmail(string email) => EmailAddressValidator.IsValid(email);

    private sealed record PlannedInvitation(int RowNumber, string Email, string RoleName, string? SuggestedUserName);

    public async Task CreateOwnershipInvitationAsync(
        TenantOwnershipInvitationRequestedEto input,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (input.RequestId == Guid.Empty ||
            input.TenantId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.TenantName))
        {
            throw new SharedValidationException("Ownership invitation request is invalid.");
        }

        using var tenantScope = _currentTenant.Change(input.TenantId, input.TenantName);

        var existing = await unitOfWorkDbContext.IdentityPublicInvitations
            .FirstOrDefaultAsync(
                x => x.OwnershipRequestId == input.RequestId,
                cancellationToken);
        if (existing != null)
        {
            if (existing.TenantId != input.TenantId)
            {
                throw new SharedValidationException("Ownership invitation request tenant does not match the existing invitation.");
            }

            if (input.IsResend)
            {
                if (existing.AcceptedAtUtc.HasValue || existing.CanceledAtUtc.HasValue)
                {
                    throw new SharedValidationException("Only an active ownership invitation can be resent.");
                }

                var resendRawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var now = DateTime.UtcNow;
                existing.TokenHash = ComputeSha256Hex(resendRawToken)
                    ?? throw new SharedException("Invitation token generation failed.");
                existing.CreatedAtUtc = now;
                existing.ExpiresAtUtc = now.AddHours(Math.Clamp(_options.Value.TokenTtlHours, 1, 24 * 30));
                var resendSafeBaseUrl = _options.Value.AllowedBaseUrls.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(resendSafeBaseUrl))
                {
                    throw new SharedException("Ownership invitation delivery is not configured.");
                }

                if (input.Kind == TenantOwnershipRequestKindEto.InitialOwner)
                {
                    await QueueInvitationNotificationAsync(
                        existing,
                        resendRawToken,
                        resendSafeBaseUrl,
                        useHostDelivery: true,
                        cancellationToken);
                }
                else
                {
                    await QueueInvitationNotificationAsync(
                        existing,
                        resendRawToken,
                        resendSafeBaseUrl,
                        cancellationToken: cancellationToken);
                }

                await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            }

            await PublishOwnershipInvitationCreatedAsync(
                existing,
                input.RequestId,
                input.Kind,
                cancellationToken);
            return;
        }

        var safeBaseUrl = _options.Value.AllowedBaseUrls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(safeBaseUrl))
        {
            throw new SharedException("Ownership invitation delivery is not configured.");
        }

        await EnsureRoleExistsAsync(
            input.TenantId,
            TenantWrightIdentityRoleNames.TenantAdmin,
            cancellationToken);
        var (invitation, rawToken) = await CreateInvitationEntityAsync(
            input.TenantId,
            NormalizeRequiredInput(input.TargetEmail, 256),
            TenantWrightIdentityRoleNames.TenantAdmin,
            NormalizeOptionalInput(input.SuggestedUserName, 256),
            cancelSupersededForEmail: false,
            cancellationToken: cancellationToken);
        invitation.OwnershipRequestId = input.RequestId;

        if (input.Kind == TenantOwnershipRequestKindEto.InitialOwner)
        {
            await QueueInvitationNotificationAsync(
                invitation,
                rawToken,
                safeBaseUrl,
                useHostDelivery: true,
                cancellationToken);
        }
        else
        {
            await QueueInvitationNotificationAsync(
                invitation,
                rawToken,
                safeBaseUrl,
                cancellationToken: cancellationToken);
        }

        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        await PublishOwnershipInvitationCreatedAsync(
            invitation,
            input.RequestId,
            input.Kind,
            cancellationToken);
    }

    private Task PublishOwnershipInvitationCreatedAsync(
        IdentityPublicInvitation invitation,
        Guid requestId,
        TenantOwnershipRequestKindEto requestKind,
        CancellationToken cancellationToken = default)
    {
        if (requestKind != TenantOwnershipRequestKindEto.InitialOwner)
        {
            return Task.CompletedTask;
        }

        return _eventBus.PublishEventAsync(
            new TenantOwnershipInvitationCreatedEto
            {
                RequestId = requestId,
                TenantId = invitation.TenantId,
                InvitationId = invitation.Id,
                OccurredAtUtc = invitation.CreatedAtUtc
            },
            cancellationToken: cancellationToken);
    }

    public async Task CancelOwnershipInvitationAsync(
        TenantOwnershipInvitationCanceledEto input,
        CancellationToken cancellationToken = default)
    {
        using var tenantScope = _currentTenant.Change(input.TenantId);
        if (input.RequestId == Guid.Empty)
        {
            return;
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var invitation = await unitOfWorkDbContext.IdentityPublicInvitations.FirstOrDefaultAsync(
            x => x.OwnershipRequestId == input.RequestId,
            cancellationToken);
        if (invitation == null || invitation.AcceptedAtUtc.HasValue || invitation.CanceledAtUtc.HasValue)
        {
            return;
        }

        CancelInvitation(invitation, "OwnershipRequestCanceled", DateTime.UtcNow);
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResultDto<PlatformStaffInvitationDto>> GetPlatformStaffListAsync(
        PlatformStaffInvitationListInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformStaffInvitationActor();
        input ??= new PlatformStaffInvitationListInput();
        var now = DateTime.UtcNow;
        var query = unitOfWorkDbContext.IdentityPublicInvitations
            .AsNoTracking()
            .Where(x => x.TenantId == PlatformStaffTenantId && !x.OwnershipRequestId.HasValue);

        var email = input.Email?.Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            query = query.Where(x => x.Email.Contains(email));
        }

        var status = NormalizeInvitationStatus(input.Status);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = status switch
            {
                "accepted" => query.Where(x => x.AcceptedAtUtc.HasValue),
                "canceled" => query.Where(x => x.CanceledAtUtc.HasValue),
                "expired" => query.Where(x => !x.AcceptedAtUtc.HasValue && !x.CanceledAtUtc.HasValue && x.ExpiresAtUtc <= now),
                _ => query.Where(x => !x.AcceptedAtUtc.HasValue && !x.CanceledAtUtc.HasValue && x.ExpiresAtUtc > now)
            };
        }

        if (input.ExpiresWithinDays.HasValue)
        {
            var horizon = now.AddDays(Math.Max(0, input.ExpiresWithinDays.Value));
            query = query.Where(x => x.ExpiresAtUtc <= horizon);
        }

        // This list previously ordered by CreatedAtUtc unconditionally, discarding the Sorting the input
        // inherits — a caller's sort was accepted and ignored. It now shares the mapping with
        // GetListAsync, so both invitation lists sort alike and reject the same undeclared fields.
        query = ApplySorting(
            query,
            SortingValidator.ValidateAndNormalize<PlatformStaffInvitationListInput>(input.Sorting));

        var totalCount = await query.LongCountAsync();
        var items = await query
            .Skip(Math.Max(0, input.SkipCount))
            .Take(Math.Clamp(input.MaxResultCount, 1, 200))
            .ToListAsync();

        return new PagedResultDto<PlatformStaffInvitationDto>
        {
            TotalCount = totalCount,
            Items = items.Select(MapToPlatformStaffDto).ToList()
        };
    }

    public async Task<PlatformStaffInvitationDto> CreatePlatformStaffAsync(CreatePlatformStaffInvitationInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        ArgumentNullException.ThrowIfNull(input);
        EnsurePlatformStaffInvitationActor();
        var roleName = NormalizePlatformStaffRole(input.RoleName);
        await EnsurePlatformStaffRoleExistsAsync(roleName);
        var (invitation, rawToken) = await CreateInvitationEntityAsync(
            PlatformStaffTenantId,
            NormalizeRequiredInput(input.Email, 256),
            roleName,
            NormalizeOptionalInput(input.SuggestedUserName, 256),
            cancelSupersededForEmail: true,
            tenantNameOverride: PlatformStaffInvitationName);

        await QueueInvitationNotificationAsync(invitation, rawToken, GetServerOwnedInvitationBaseUrl());
        await unitOfWorkDbContext.SaveChangesAsync();
        return MapToPlatformStaffDto(invitation);
    }

    public async Task<PlatformStaffInvitationDto> ResendPlatformStaffAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformStaffInvitationActor();
        var invitation = await FindPlatformStaffInvitationAsync(id);
        if (invitation == null)
        {
            throw new SharedValidationException("Platform staff invitation was not found.");
        }

        if (invitation.AcceptedAtUtc.HasValue)
        {
            throw new SharedValidationException("Accepted platform staff invitations cannot be resent.");
        }

        var roleName = NormalizePlatformStaffRole(invitation.RoleName);
        await EnsurePlatformStaffRoleExistsAsync(roleName);
        CancelInvitation(invitation, "Superseded", DateTime.UtcNow);
        var (replacement, rawToken) = await CreateInvitationEntityAsync(
            PlatformStaffTenantId,
            invitation.Email,
            roleName,
            invitation.SuggestedUserName,
            cancelSupersededForEmail: false,
            tenantNameOverride: PlatformStaffInvitationName);

        await QueueInvitationNotificationAsync(replacement, rawToken, GetServerOwnedInvitationBaseUrl());
        await unitOfWorkDbContext.SaveChangesAsync();
        return MapToPlatformStaffDto(replacement);
    }

    public async Task CancelPlatformStaffAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformStaffInvitationActor();
        var invitation = await FindPlatformStaffInvitationAsync(id);
        if (invitation == null || invitation.CanceledAtUtc.HasValue)
        {
            return;
        }

        if (invitation.AcceptedAtUtc.HasValue)
        {
            throw new SharedValidationException("Accepted platform staff invitations cannot be canceled.");
        }

        CancelInvitation(invitation, "Canceled", DateTime.UtcNow);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    public async Task<InvitationDto> ResendAsync(Guid id, ResendInvitationInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var safeBaseUrl = ResolveAllowedBaseUrl(input.AcceptanceBaseUrl);
        var invitation = await unitOfWorkDbContext.IdentityPublicInvitations
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);

        if (invitation == null)
        {
            throw new SharedValidationException("Invitation was not found.");
        }

        if (invitation.AcceptedAtUtc.HasValue)
        {
            throw new SharedValidationException("Accepted invitations cannot be resent.");
        }

        if (!string.IsNullOrWhiteSpace(invitation.RoleName))
        {
            var normalizedRoleName = _managementGuard.NormalizeSingleRole(invitation.RoleName);
            _managementGuard.EnsureCanAssignRoles([normalizedRoleName]);
            await EnsureRoleExistsAsync(tenantId, normalizedRoleName);
            invitation.RoleName = normalizedRoleName;
        }

        CancelInvitation(invitation, "Superseded", DateTime.UtcNow);
        var (replacement, rawToken) = await CreateInvitationEntityAsync(
            tenantId,
            invitation.Email,
            invitation.RoleName,
            invitation.SuggestedUserName,
            cancelSupersededForEmail: false);

        await QueueInvitationNotificationAsync(replacement, rawToken, safeBaseUrl);
        await unitOfWorkDbContext.SaveChangesAsync();

        return MapToDto(replacement);
    }

    public async Task CancelAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _managementGuard.RequireTenantId();
        var invitation = await unitOfWorkDbContext.IdentityPublicInvitations
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);

        if (invitation == null || invitation.CanceledAtUtc.HasValue)
        {
            return;
        }

        if (invitation.AcceptedAtUtc.HasValue)
        {
            throw new SharedValidationException("Accepted invitations cannot be canceled.");
        }

        if (!_managementGuard.IsCurrentUserHostAdmin() &&
            TenantWrightIdentityRoleNames.IsTenantAdminRole(invitation.RoleName))
        {
            throw new UnauthorizedAccessException("Tenant-admins cannot manage elevated invitations.");
        }

        CancelInvitation(invitation, "Canceled", DateTime.UtcNow);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    private async Task<(IdentityPublicInvitation Invitation, string RawToken)> CreateInvitationEntityAsync(
        Guid tenantId,
        string email,
        string? roleName,
        string? suggestedUserName,
        bool cancelSupersededForEmail,
        string? tenantNameOverride = null,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        if (cancelSupersededForEmail)
        {
            var activeInvitations = await unitOfWorkDbContext.IdentityPublicInvitations
                .Where(x =>
                    x.TenantId == tenantId &&
                    x.Email == email &&
                    !x.AcceptedAtUtc.HasValue &&
                    !x.CanceledAtUtc.HasValue &&
                    x.ExpiresAtUtc > now)
                .ToListAsync(cancellationToken);

            foreach (var activeInvitation in activeInvitations)
            {
                CancelInvitation(activeInvitation, "Superseded", now);
            }
        }

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tenantName = tenantNameOverride ?? _currentTenant.Name?.Trim();

        if (string.IsNullOrWhiteSpace(tenantName))
        {
            throw new SharedValidationException("Current tenant context is invalid.");
        }

        var invitation = new IdentityPublicInvitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TenantName = tenantName,
            Email = email,
            TokenHash = ComputeSha256Hex(rawToken) ?? throw new SharedException("Invitation token generation failed."),
            RoleName = roleName,
            SuggestedUserName = suggestedUserName,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(Math.Clamp(_options.Value.TokenTtlHours, 1, 24 * 30))
        };

        await unitOfWorkDbContext.AddAsync(invitation, cancellationToken);
        return (invitation, rawToken);
    }

    private async Task QueueInvitationNotificationAsync(
        IdentityPublicInvitation invitation,
        string rawToken,
        string safeBaseUrl,
        bool useHostDelivery = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new SharedValidationException("Invitation token is unavailable.");
        }

        var link = $"{safeBaseUrl}{AcceptInvitationPublicPath}?token={Uri.EscapeDataString(rawToken)}";

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
                        Subject = _options.Value.Subject,
                        TemplateName = _options.Value.TemplateName,
                        TemplateModel = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["tenantName"] = invitation.TenantName,
                ["email"] = invitation.Email,
                ["roleName"] = invitation.RoleName,
                ["link"] = link
            }
                    }]
            },
            Recipients = IdentityNotificationContractPublisher.CreateSingleChannelRecipients(NotificationSendChannel.Email, [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = invitation.Email
                }
            ], null),
        };

        Guid? tenantId = useHostDelivery || invitation.TenantId == Guid.Empty
            ? null
            : invitation.TenantId;
        await IdentityNotificationContractPublisher.PublishAsync(
            _eventBus,
            tenantId,
            command,
            cancellationToken);
    }

    private async Task EnsureRoleExistsAsync(
        Guid tenantId,
        string roleName,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoleName = roleName.ToUpperInvariant();
        var exists = await _roleManager.Roles.AnyAsync(
            x => x.TenantId == tenantId && x.NormalizedName == normalizedRoleName,
            cancellationToken);
        if (!exists)
        {
            if (!TenantWrightIdentityRoleNames.IsTenantAdminRole(roleName))
            {
                throw new SharedValidationException("Invitation role assignment is invalid.");
            }

            var role = new ApplicationRole(TenantWrightIdentityRoleNames.TenantAdmin)
            {
                Id = IdentitySystemRoleIds.TenantAdmin(tenantId),
                TenantId = tenantId
            };

            var createResult = await _roleManager.CreateAsync(role);
            if (!createResult.Succeeded)
            {
                exists = await _roleManager.Roles.AnyAsync(
                    x => x.TenantId == tenantId && x.NormalizedName == normalizedRoleName,
                    cancellationToken);
                if (!exists)
                {
                    throw new SharedValidationException("Invitation role assignment is invalid.");
                }
            }
        }
    }

    private InvitationDto MapToDto(IdentityPublicInvitation invitation)
    {
        var now = DateTime.UtcNow;
        return new InvitationDto
        {
            Id = invitation.Id,
            TenantId = invitation.TenantId,
            TenantName = invitation.TenantName,
            Email = invitation.Email,
            RoleName = TenantWrightIdentityRoleNames.Normalize(invitation.RoleName),
            SuggestedUserName = invitation.SuggestedUserName,
            Status = ResolveStatus(invitation, now),
            CreatedAtUtc = invitation.CreatedAtUtc,
            ExpiresAtUtc = invitation.ExpiresAtUtc,
            AcceptedAtUtc = invitation.AcceptedAtUtc,
            AcceptedUserId = invitation.AcceptedUserId,
            CanceledAtUtc = invitation.CanceledAtUtc
        };
    }

    private PlatformStaffInvitationDto MapToPlatformStaffDto(IdentityPublicInvitation invitation)
    {
        var source = MapToDto(invitation);
        return new PlatformStaffInvitationDto
        {
            Id = source.Id,
            TenantId = source.TenantId,
            TenantName = source.TenantName,
            Email = source.Email,
            RoleName = source.RoleName,
            SuggestedUserName = source.SuggestedUserName,
            Status = source.Status,
            CreatedAtUtc = source.CreatedAtUtc,
            ExpiresAtUtc = source.ExpiresAtUtc,
            AcceptedAtUtc = source.AcceptedAtUtc,
            AcceptedUserId = source.AcceptedUserId,
            CanceledAtUtc = source.CanceledAtUtc,
            DeliveryState = source.Status == "pending" ? "Queued" : "NotApplicable"
        };
    }

    private void CancelInvitation(IdentityPublicInvitation invitation, string reason, DateTime now)
    {
        invitation.CanceledAtUtc = now;
        invitation.CanceledByUserId = _currentUser.Id;
        invitation.CancelReason = reason;
    }

    private string ResolveAllowedBaseUrl(string? baseUrl)
    {
        var allowedBaseUrl = NormalizeAndValidatePublicBaseUrl(baseUrl, _options.Value.AllowedBaseUrls);
        if (string.IsNullOrWhiteSpace(allowedBaseUrl))
        {
            throw new SharedValidationException("Invitation base URL is invalid.");
        }

        return allowedBaseUrl;
    }

    private string GetServerOwnedInvitationBaseUrl()
    {
        var baseUrl = _options.Value.AllowedBaseUrls.FirstOrDefault();
        var safeBaseUrl = NormalizeAndValidatePublicBaseUrl(baseUrl, _options.Value.AllowedBaseUrls);
        if (string.IsNullOrWhiteSpace(safeBaseUrl))
        {
            throw new SharedException("Platform staff invitation delivery is not configured.");
        }

        return safeBaseUrl;
    }

    private void EnsurePlatformStaffInvitationActor()
    {
        _managementGuard.EnsureHostManagementActor();
        if (!_managementGuard.IsCurrentUserHostAdmin())
        {
            throw new UnauthorizedAccessException("Platform staff invitations require host admin context.");
        }
    }

    private static string NormalizePlatformStaffRole(string? roleName)
    {
        var normalizedRoleName = TenantWrightIdentityRoleNames.Normalize(roleName);
        if (string.IsNullOrWhiteSpace(normalizedRoleName))
        {
            throw new SharedValidationException("A platform role is required.");
        }

        if (TenantWrightIdentityRoleNames.IsHostAdminRole(normalizedRoleName))
        {
            throw new UnauthorizedAccessException("Platform staff invitations cannot assign host administrator.");
        }

        return normalizedRoleName;
    }

    private async Task EnsurePlatformStaffRoleExistsAsync(string roleName)
    {
        var normalizedRoleName = roleName.ToUpperInvariant();
        var exists = await _roleManager.Roles.AnyAsync(x =>
            x.TenantId == null &&
            x.NormalizedName == normalizedRoleName);
        if (!exists)
        {
            throw new SharedValidationException("Platform invitation role assignment is invalid.");
        }
    }

    private async Task<IdentityPublicInvitation?> FindPlatformStaffInvitationAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        return await unitOfWorkDbContext.IdentityPublicInvitations.FirstOrDefaultAsync(x =>
                    x.Id == id &&
                    x.TenantId == PlatformStaffTenantId &&
                    !x.OwnershipRequestId.HasValue);
    }

    private static string NormalizeInvitationStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string ResolveStatus(IdentityPublicInvitation invitation, DateTime now)
    {
        if (invitation.AcceptedAtUtc.HasValue)
        {
            return "accepted";
        }

        if (invitation.CanceledAtUtc.HasValue)
        {
            return "canceled";
        }

        return invitation.ExpiresAtUtc <= now ? "expired" : "pending";
    }

    private static IQueryable<IdentityPublicInvitation> ApplySorting(IQueryable<IdentityPublicInvitation> query, string? sorting)
    {
        var normalized = sorting?.Trim().ToLowerInvariant();
        return normalized switch
        {
            // Direction-complete, and a bare field name always means ascending. "expiresatutc" had no
            // bare case and CreatedAtUtc was reachable only as the fallback, so neither could be
            // requested explicitly.
            "email" or "email asc" => query.OrderBy(x => x.Email).ThenByDescending(x => x.CreatedAtUtc),
            "email desc" => query.OrderByDescending(x => x.Email).ThenByDescending(x => x.CreatedAtUtc),
            "expiresatutc" or "expiresatutc asc" => query.OrderBy(x => x.ExpiresAtUtc).ThenByDescending(x => x.CreatedAtUtc),
            "expiresatutc desc" => query.OrderByDescending(x => x.ExpiresAtUtc).ThenByDescending(x => x.CreatedAtUtc),
            "createdatutc" or "createdatutc asc" => query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            "createdatutc desc" => query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
            _ => query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
        };
    }

    private static string NormalizeRequiredInput(string value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new SharedValidationException("Required field is missing.");
        }

        if (normalized.Length > maxLength)
        {
            throw new SharedValidationException($"Field length must be equal to or lower than {maxLength}.");
        }

        return normalized;
    }

    private static string? NormalizeOptionalInput(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length > maxLength)
        {
            throw new SharedValidationException($"Field length must be equal to or lower than {maxLength}.");
        }

        return normalized;
    }

    private static string? NormalizeAndValidatePublicBaseUrl(string? value, IReadOnlyCollection<string> allowedBaseUrls)
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

        foreach (var allowed in allowedBaseUrls)
        {
            var normalizedAllowed = NormalizeAbsoluteUrl(allowed);
            if (!string.IsNullOrWhiteSpace(normalizedAllowed) &&
                string.Equals(candidate, normalizedAllowed, StringComparison.OrdinalIgnoreCase))
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

    private static string? ComputeSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
