using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System.Text.Json;
using Identity.Application.Account.Risk;
using Identity.Application.Contracts.LoginRisk;
using Identity.Application.SecurityLogs;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.Constants;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.LoginRisk;

public class LoginRiskAdminAppService : ILoginRiskAdminAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;

    public LoginRiskAdminAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, ICurrentUser currentUser)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
    }

    [Audited]
    public async Task<LoginRiskOverrideDto> CreateOverrideAsync(LoginRiskOverrideCreateInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (input.UserId == Guid.Empty)
        {
            throw new ArgumentException("UserId is required.", nameof(input));
        }

        if (input.ExpiresAtUtc.HasValue && input.ExpiresAtUtc.Value <= DateTime.UtcNow)
        {
            throw new ArgumentException("ExpiresAtUtc must be in the future when provided.", nameof(input));
        }

        var userExists = await ApplyCurrentUserScope(unitOfWorkDbContext.Users.AsQueryable())
            .AnyAsync(x => x.Id == input.UserId);
        if (!userExists)
        {
            throw new EntityNotFoundException<ApplicationUser>(input.UserId);
        }

        var entity = new IdentityLoginRiskOverride
        {
            Id = Guid.CreateVersion7(),
            TenantId = _currentTenant.Id,
            UserId = input.UserId,
            ModeOverride = input.ModeOverride,
            Reason = input.Reason,
            CreatedBy = _currentUser.Id,
            CreationTime = DateTime.UtcNow,
            ExpiresAtUtc = input.ExpiresAtUtc,
            IsActive = true
        };

        await unitOfWorkDbContext.AddAsync(entity);
        await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = _currentTenant.Id,
            ApplicationName = "Identity",
            Identity = "LoginRisk",
            Action = "LoginRiskOverrideCreated",
            UserId = _currentUser.Id,
            UserName = _currentUser.UserName,
            CreationTime = DateTime.UtcNow,
            ExtraProperties = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["overrideId"] = entity.Id,
                ["targetUserId"] = entity.UserId,
                ["modeOverride"] = entity.ModeOverride.ToString(),
                ["expiresAtUtc"] = entity.ExpiresAtUtc,
                ["reason"] = entity.Reason
            })
        });

        await unitOfWorkDbContext.SaveChangesAsync();
        return MapToDto(entity);
    }

    public async Task<List<LoginRiskOverrideDto>> GetOverridesAsync(Guid? userId = null, bool includeInactive = false)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var query = ApplyCurrentTenantScope(unitOfWorkDbContext.IdentityLoginRiskOverrides.AsQueryable());

        if (userId.HasValue && userId.Value != Guid.Empty)
        {
            query = query.Where(x => x.UserId == userId.Value);
        }

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive && (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc.Value > now));
        }

        var list = await query
            .OrderByDescending(x => x.CreationTime)
            .Take(500)
            .ToListAsync();

        return await MapToDtosAsync(list);
    }

    public async Task<PagedLoginRiskOverridesDto> GetPagedOverridesAsync(LoginRiskOverrideListInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input ??= new LoginRiskOverrideListInput();
        var now = DateTime.UtcNow;
        var query = ApplyCurrentTenantScope(unitOfWorkDbContext.IdentityLoginRiskOverrides.AsNoTracking());
        if (input.UserId is { } userId && userId != Guid.Empty)
        {
            query = query.Where(x => x.UserId == userId);
        }

        if (input.ModeOverride.HasValue)
        {
            query = query.Where(x => x.ModeOverride == input.ModeOverride.Value);
        }

        if (!input.IncludeInactive)
        {
            query = query.Where(x => x.IsActive && (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc > now));
        }

        var totalCount = await query.LongCountAsync();
        var entities = await query
            .OrderByDescending(x => x.CreationTime)
            .Skip(Math.Max(0, input.SkipCount))
            .Take(Math.Clamp(input.MaxResultCount, 1, 200))
            .ToListAsync();
        return new PagedLoginRiskOverridesDto
        {
            TotalCount = totalCount,
            Items = await MapToDtosAsync(entities)
        };
    }

    [Audited]
    public async Task RemoveOverrideAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var entity = await ApplyCurrentTenantScope(unitOfWorkDbContext.IdentityLoginRiskOverrides.AsQueryable())
                    .FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null)
        {
            return;
        }

        entity.IsActive = false;

        await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = _currentTenant.Id,
            ApplicationName = "Identity",
            Identity = "LoginRisk",
            Action = "LoginRiskOverrideRemoved",
            UserId = _currentUser.Id,
            UserName = _currentUser.UserName,
            CreationTime = DateTime.UtcNow,
            ExtraProperties = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["overrideId"] = entity.Id,
                ["targetUserId"] = entity.UserId
            })
        });

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [Audited]
    public async Task MarkFalsePositiveAsync(Guid securityLogId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var securityLog = await ApplyCurrentTenantScope(unitOfWorkDbContext.IdentitySecurityLogs.AsQueryable())
                    .FirstOrDefaultAsync(x => x.Id == securityLogId);
        if (securityLog == null)
        {
            throw new EntityNotFoundException<IdentitySecurityLog>(securityLogId);
        }

        if (!string.Equals(securityLog.Action, "LoginRiskBlocked", StringComparison.Ordinal))
        {
            throw new SharedConflictException(
                IdentityErrorCodes.LoginRiskNotBlocked,
                "Only LoginRiskBlocked decisions can be marked as false positive.");
        }

        var properties = ReadExtraProperties(securityLog.ExtraProperties);
        properties["falsePositive"] = true;
        properties["falsePositiveMarkedAtUtc"] = DateTime.UtcNow;
        properties["falsePositiveMarkedBy"] = _currentUser.Id?.ToString("D");
        securityLog.ExtraProperties = JsonSerializer.Serialize(properties);
        IdentitySecurityLogRiskMetadata.ApplyScalarFields(securityLog);

        await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = _currentTenant.Id,
            ApplicationName = "Identity",
            Identity = "LoginRisk",
            Action = "LoginRiskFalsePositiveMarked",
            UserId = _currentUser.Id,
            UserName = _currentUser.UserName,
            CreationTime = DateTime.UtcNow,
            ExtraProperties = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["securityLogId"] = securityLogId
            })
        });

        LoginRiskMetrics.RecordFalsePositive();
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    private IQueryable<T> ApplyCurrentTenantScope<T>(IQueryable<T> query)
        where T : class, Shared.Domain.IMultiTenant
    {
        if (_currentTenant.Id.HasValue)
        {
            return query.Where(x => x.TenantId == _currentTenant.Id.Value);
        }

        if (_currentUser.IsAuthenticated)
        {
            return query.Where(x => x.TenantId == null);
        }

        return query;
    }

    private IQueryable<ApplicationUser> ApplyCurrentUserScope(IQueryable<ApplicationUser> query)
    {
        return _currentTenant.Id.HasValue
            ? query.Where(user => user.TenantId == _currentTenant.Id.Value)
            : query.Where(user => user.TenantId == Guid.Empty);
    }

    private static Dictionary<string, object?> ReadExtraProperties(string? extraPropertiesJson)
    {
        return IdentitySecurityLogRiskMetadata.ReadDictionary(extraPropertiesJson);
    }

    private async Task<List<LoginRiskOverrideDto>> MapToDtosAsync(List<IdentityLoginRiskOverride> entities)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (entities.Count == 0)
        {
            return [];
        }

        var userIds = entities.Select(entity => entity.UserId).Distinct().ToList();
        var users = await ApplyCurrentUserScope(unitOfWorkDbContext.Users.AsNoTracking())
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new UserDisplayProjection(user.Id, user.UserName, user.Email))
            .ToDictionaryAsync(user => user.Id);
        return entities.Select(entity => MapToDto(entity, users.GetValueOrDefault(entity.UserId))).ToList();
    }

    private static LoginRiskOverrideDto MapToDto(IdentityLoginRiskOverride entity, UserDisplayProjection? user = null)
    {
        return new LoginRiskOverrideDto
        {
            Id = entity.Id,
            UserId = entity.UserId,
            ModeOverride = entity.ModeOverride,
            Reason = entity.Reason,
            CreatedBy = entity.CreatedBy,
            CreationTime = entity.CreationTime,
            ExpiresAtUtc = entity.ExpiresAtUtc,
            IsActive = entity.IsActive,
            UserName = user?.UserName,
            UserEmail = user?.Email
        };
    }

    private sealed record UserDisplayProjection(Guid Id, string? UserName, string? Email);
}
