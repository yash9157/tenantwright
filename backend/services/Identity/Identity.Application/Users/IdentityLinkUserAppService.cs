using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Application.Contracts.Users;
using Identity.Application.Tenancy;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

public class IdentityLinkUserAppService : IIdentityLinkUserAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;

    public IdentityLinkUserAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentUser currentUser, ICurrentTenant currentTenant)
    {
        _dbContextProvider = dbContextProvider;
        _currentUser = currentUser;
        _currentTenant = currentTenant;
    }

    public async Task<List<IdentityLinkUserDto>> GetListAsync()
    {
        var currentUserId = RequireCurrentUserId();
        return await GetListForUserAsync(currentUserId);
    }

    public async Task<List<IdentityLinkUserDto>> GetListForUserAsync(Guid userId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (userId == Guid.Empty)
        {
            throw new SharedValidationException("User is required.");
        }

        var tenantId = _currentTenant.RequireTenantId();
        await RequireActiveTenantUserAsync(userId, tenantId, "Selected user does not exist in the current tenant.");

        var links = await unitOfWorkDbContext.IdentityLinkUsers
            .AsNoTracking()
            .Where(x =>
                x.SourceTenantId == tenantId &&
                x.TargetTenantId == tenantId &&
                (x.SourceUserId == userId || x.TargetUserId == userId))
            .OrderByDescending(x => x.Id)
            .ToListAsync();

        return await MapLinksAsync(links, userId);
    }

    public async Task<List<IdentityLinkUserRequestDto>> GetRequestsAsync()
    {
        var currentUserId = RequireCurrentUserId();
        return await GetRequestsForUserAsync(currentUserId);
    }

    public async Task<List<IdentityLinkUserRequestDto>> GetRequestsForUserAsync(Guid userId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (userId == Guid.Empty)
        {
            throw new SharedValidationException("User is required.");
        }

        var tenantId = _currentTenant.RequireTenantId();
        await RequireActiveTenantUserAsync(userId, tenantId, "Selected user does not exist in the current tenant.");

        var requests = await unitOfWorkDbContext.IdentityLinkUserRequests
            .AsNoTracking()
            .Where(x =>
                x.TenantId == tenantId &&
                (x.RequestedByUserId == userId || x.TargetUserId == userId))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync();

        return await MapRequestsAsync(requests, userId);
    }

    [Audited]
    public async Task CreateRequestAsync(CreateLinkUserRequestInput input)
    {
        var currentUserId = RequireCurrentUserId();
        await CreateRequestForUserAsync(new CreateLinkUserRequestForUserInput
        {
            SourceUserId = currentUserId,
            TargetUserId = input.TargetUserId
        });
    }

    [Audited]
    public async Task CreateRequestForUserAsync(CreateLinkUserRequestForUserInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var sourceUserId = input.SourceUserId;
        var targetUserId = input.TargetUserId;

        if (sourceUserId == Guid.Empty)
        {
            throw new SharedValidationException("Source user is required.");
        }

        if (targetUserId == Guid.Empty)
        {
            throw new SharedValidationException("Target user is required.");
        }

        if (targetUserId == sourceUserId)
        {
            throw new SharedValidationException("You cannot link your own account.");
        }

        await RequireActiveTenantUserAsync(sourceUserId, tenantId, "Source user does not exist in the current tenant.");

        var targetUser = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == targetUserId && x.TenantId == tenantId);
        if (targetUser == null)
        {
            throw new SharedValidationException("Target user does not exist in the current tenant.");
        }

        if (targetUser.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
        {
            throw new SharedValidationException("Target user must be active.");
        }

        await EnsureNoActiveLinkAsync(sourceUserId, targetUserId, tenantId);

        var hasPendingRequest = await unitOfWorkDbContext.IdentityLinkUserRequests.AnyAsync(x =>
            x.TenantId == tenantId &&
            x.Status == IdentityLinkUserApprovalStatuses.Pending &&
            ((x.RequestedByUserId == sourceUserId && x.TargetUserId == targetUserId) ||
             (x.RequestedByUserId == targetUserId && x.TargetUserId == sourceUserId)));

        if (hasPendingRequest)
        {
            throw new SharedValidationException("A pending link request already exists for the selected user.");
        }

        await unitOfWorkDbContext.AddAsync(new IdentityLinkUserApproval
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RequestedByUserId = sourceUserId,
            TargetUserId = targetUserId,
            Status = IdentityLinkUserApprovalStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        });

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [Audited]
    public async Task ApproveRequestAsync(Guid id)
    {
        var currentUserId = RequireCurrentUserId();
        await ApproveRequestForUserAsync(id, new LinkUserRequestAdminDecisionInput
        {
            UserId = currentUserId
        });
    }

    [Audited]
    public async Task ApproveRequestForUserAsync(Guid id, LinkUserRequestAdminDecisionInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var actingUserId = input.UserId;

        if (actingUserId == Guid.Empty)
        {
            throw new SharedValidationException("User is required.");
        }

        var request = await unitOfWorkDbContext.IdentityLinkUserRequests.FirstOrDefaultAsync(x =>
            x.Id == id &&
            x.TenantId == tenantId &&
            x.TargetUserId == actingUserId);

        if (request == null)
        {
            throw new SharedValidationException("Link request was not found.");
        }

        if (!string.Equals(request.Status, IdentityLinkUserApprovalStatuses.Pending, StringComparison.Ordinal))
        {
            throw new SharedValidationException("Only pending link requests can be approved.");
        }

        var requester = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == request.RequestedByUserId && x.TenantId == tenantId);
        var approver = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == actingUserId && x.TenantId == tenantId);
        if (requester == null ||
            requester.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active ||
            approver == null ||
            approver.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
        {
            throw new SharedValidationException("Both accounts must remain active in the current tenant.");
        }

        await EnsureNoActiveLinkAsync(request.RequestedByUserId, request.TargetUserId, tenantId);

        await unitOfWorkDbContext.AddAsync(new IdentityLinkUser
        {
            Id = Guid.CreateVersion7(),
            SourceUserId = request.RequestedByUserId,
            SourceTenantId = tenantId,
            TargetUserId = request.TargetUserId,
            TargetTenantId = tenantId
        });

        request.Status = IdentityLinkUserApprovalStatuses.Approved;
        request.RespondedAtUtc = DateTime.UtcNow;
        request.RespondedByUserId = actingUserId;
        request.RejectionReason = null;

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [Audited]
    public async Task RejectRequestAsync(Guid id, RejectLinkUserRequestInput? input = null)
    {
        var currentUserId = RequireCurrentUserId();
        await RejectRequestForUserAsync(id, new RejectLinkUserRequestForUserInput
        {
            UserId = currentUserId,
            Reason = input?.Reason
        });
    }

    [Audited]
    public async Task RejectRequestForUserAsync(Guid id, RejectLinkUserRequestForUserInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var actingUserId = input.UserId;

        if (actingUserId == Guid.Empty)
        {
            throw new SharedValidationException("User is required.");
        }

        var request = await unitOfWorkDbContext.IdentityLinkUserRequests.FirstOrDefaultAsync(x =>
            x.Id == id &&
            x.TenantId == tenantId &&
            x.TargetUserId == actingUserId);

        if (request == null)
        {
            throw new SharedValidationException("Link request was not found.");
        }

        if (!string.Equals(request.Status, IdentityLinkUserApprovalStatuses.Pending, StringComparison.Ordinal))
        {
            throw new SharedValidationException("Only pending link requests can be rejected.");
        }

        request.Status = IdentityLinkUserApprovalStatuses.Rejected;
        request.RespondedAtUtc = DateTime.UtcNow;
        request.RespondedByUserId = actingUserId;
        request.RejectionReason = NormalizeOptional(input?.Reason, 512);

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [Audited]
    public Task LinkAsync(LinkUserDto input)
    {
        ValidateTenantAliasInput(input.TargetTenantId);
        return CreateRequestAsync(new CreateLinkUserRequestInput
        {
            TargetUserId = input.TargetUserId
        });
    }

    [Audited]
    public async Task UnlinkAsync(LinkUserDto input)
    {
        var currentUserId = RequireCurrentUserId();
        ValidateTenantAliasInput(input.TargetTenantId);
        await UnlinkForUserAsync(new LinkUserAdminUnlinkInput
        {
            SourceUserId = currentUserId,
            TargetUserId = input.TargetUserId
        });
    }

    [Audited]
    public async Task UnlinkForUserAsync(LinkUserAdminUnlinkInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();

        if (input.SourceUserId == Guid.Empty)
        {
            throw new SharedValidationException("Source user is required.");
        }

        if (input.TargetUserId == Guid.Empty)
        {
            throw new SharedValidationException("Target user is required.");
        }

        await RequireActiveTenantUserAsync(input.SourceUserId, tenantId, "Source user does not exist in the current tenant.");
        await RequireActiveTenantUserAsync(input.TargetUserId, tenantId, "Target user does not exist in the current tenant.");

        var linkedUser = await unitOfWorkDbContext.IdentityLinkUsers.FirstOrDefaultAsync(x =>
            x.SourceTenantId == tenantId &&
            x.TargetTenantId == tenantId &&
            ((x.SourceUserId == input.SourceUserId && x.TargetUserId == input.TargetUserId) ||
             (x.SourceUserId == input.TargetUserId && x.TargetUserId == input.SourceUserId)));

        if (linkedUser == null)
        {
            return;
        }

        unitOfWorkDbContext.Remove(linkedUser);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    private Guid RequireCurrentUserId()
    {
        return _currentUser.Id ?? throw new SharedUnauthorizedException("User is not authenticated.");
    }

    private void ValidateTenantAliasInput(Guid? targetTenantId)
    {
        if (targetTenantId.HasValue && targetTenantId.Value != _currentTenant.RequireTenantId())
        {
            throw new SharedValidationException("Linked accounts are same-tenant only.");
        }
    }

    private async Task RequireActiveTenantUserAsync(Guid userId, Guid tenantId, string notFoundMessage)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId);
        if (user == null)
        {
            throw new SharedValidationException(notFoundMessage);
        }

        if (user.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
        {
            throw new SharedValidationException("Selected user must be active.");
        }
    }

    private async Task EnsureNoActiveLinkAsync(Guid sourceUserId, Guid targetUserId, Guid tenantId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var isLinked = await unitOfWorkDbContext.IdentityLinkUsers.AnyAsync(x =>
                    x.SourceTenantId == tenantId &&
                    x.TargetTenantId == tenantId &&
                    ((x.SourceUserId == sourceUserId && x.TargetUserId == targetUserId) ||
                     (x.SourceUserId == targetUserId && x.TargetUserId == sourceUserId)));

        if (isLinked)
        {
            throw new SharedValidationException("The selected accounts are already linked.");
        }
    }

    private async Task<List<IdentityLinkUserDto>> MapLinksAsync(List<IdentityLinkUser> links, Guid currentUserId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (links.Count == 0)
        {
            return [];
        }

        var userIds = links
            .SelectMany(x => new[] { x.SourceUserId, x.TargetUserId })
            .Distinct()
            .ToList();

        var users = await unitOfWorkDbContext.Users
            .AsNoTracking()
            .Where(x => userIds.Contains(x.Id))
            .Select(x => new LinkedUserLookup
            {
                Id = x.Id,
                UserName = x.UserName,
                Email = x.Email
            })
            .ToDictionaryAsync(x => x.Id);

        return links.Select(link =>
        {
            var counterpartUserId = link.SourceUserId == currentUserId ? link.TargetUserId : link.SourceUserId;
            users.TryGetValue(counterpartUserId, out var counterpart);

            return new IdentityLinkUserDto
            {
                Id = link.Id,
                SourceUserId = link.SourceUserId,
                SourceTenantId = link.SourceTenantId,
                TargetUserId = link.TargetUserId,
                TargetTenantId = link.TargetTenantId,
                CounterpartUserId = counterpartUserId,
                CounterpartUserName = counterpart?.UserName,
                CounterpartEmail = counterpart?.Email
            };
        }).ToList();
    }

    private async Task<List<IdentityLinkUserRequestDto>> MapRequestsAsync(List<IdentityLinkUserApproval> requests, Guid currentUserId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (requests.Count == 0)
        {
            return [];
        }

        var userIds = requests
            .SelectMany(x => new[] { x.RequestedByUserId, x.TargetUserId })
            .Distinct()
            .ToList();

        var users = await unitOfWorkDbContext.Users
            .AsNoTracking()
            .Where(x => userIds.Contains(x.Id))
            .Select(x => new LinkedUserLookup
            {
                Id = x.Id,
                UserName = x.UserName,
                Email = x.Email
            })
            .ToDictionaryAsync(x => x.Id);

        return requests.Select(request =>
        {
            users.TryGetValue(request.RequestedByUserId, out var requester);
            users.TryGetValue(request.TargetUserId, out var target);

            return new IdentityLinkUserRequestDto
            {
                Id = request.Id,
                TenantId = request.TenantId ?? Guid.Empty,
                RequestedByUserId = request.RequestedByUserId,
                RequestedByUserName = requester?.UserName,
                RequestedByEmail = requester?.Email,
                TargetUserId = request.TargetUserId,
                TargetUserName = target?.UserName,
                TargetEmail = target?.Email,
                Status = request.Status,
                CreatedAtUtc = request.CreatedAtUtc,
                RespondedAtUtc = request.RespondedAtUtc,
                RejectionReason = request.RejectionReason,
                IsIncoming = request.TargetUserId == currentUserId
            };
        }).ToList();
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length > maxLength)
        {
            throw new SharedValidationException($"Value length must be equal to or lower than {maxLength}.");
        }

        return normalized;
    }

    private sealed class LinkedUserLookup
    {
        public Guid Id { get; init; }
        public string? UserName { get; init; }
        public string? Email { get; init; }
    }
}
