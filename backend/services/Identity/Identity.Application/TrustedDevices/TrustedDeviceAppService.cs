using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Identity.Application.Contracts.TrustedDevices;
using Identity.Domain.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.AuditLogs.Auditing;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.TrustedDevices;

public class TrustedDeviceAppService : ITrustedDeviceAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;

    public TrustedDeviceAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentUser currentUser, ICurrentTenant currentTenant)
    {
        _dbContextProvider = dbContextProvider;
        _currentUser = currentUser;
        _currentTenant = currentTenant;
    }

    public async Task<List<TrustedDeviceDto>> GetMyTrustedDevicesAsync(bool includeExpired = false)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userId = _currentUser.Id;
        if (!userId.HasValue)
        {
            return [];
        }

        var now = DateTime.UtcNow;
        var query = ApplyCurrentTenantScope(unitOfWorkDbContext.IdentityTrustedDevices.AsQueryable())
            .Where(x => x.UserId == userId.Value);

        if (!includeExpired)
        {
            query = query.Where(x => x.ExpiresAtUtc > now);
        }

        var devices = await query
            .OrderByDescending(x => x.LastSeenUtc)
            .Take(200)
            .ToListAsync();

        return devices
            .Select(x => new TrustedDeviceDto
            {
                Id = x.Id,
                DeviceKey = BuildDeviceKey(x.DeviceHash),
                DisplayLabel = $"Trusted device {BuildDeviceKey(x.DeviceHash)}",
                FirstSeenUtc = x.FirstSeenUtc,
                LastSeenUtc = x.LastSeenUtc,
                ExpiresAtUtc = x.ExpiresAtUtc,
                IsExpired = x.ExpiresAtUtc <= now
            })
            .ToList();
    }

    [Audited]
    public async Task RevokeMyTrustedDeviceAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userId = _currentUser.Id;
        if (!userId.HasValue || id == Guid.Empty)
        {
            return;
        }

        var device = await ApplyCurrentTenantScope(unitOfWorkDbContext.IdentityTrustedDevices.AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId.Value);
        if (device == null)
        {
            return;
        }

        unitOfWorkDbContext.Remove(device);
        await WriteAuditSecurityLogAsync(
            action: "TrustedDeviceRevoked",
            extraProperties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["trustedDeviceId"] = device.Id,
                ["deviceKey"] = BuildDeviceKey(device.DeviceHash)
            });

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [Audited]
    public async Task<int> RevokeAllMyTrustedDevicesAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userId = _currentUser.Id;
        if (!userId.HasValue)
        {
            return 0;
        }

        var devices = await ApplyCurrentTenantScope(unitOfWorkDbContext.IdentityTrustedDevices.AsQueryable())
            .Where(x => x.UserId == userId.Value)
            .ToListAsync();

        if (devices.Count == 0)
        {
            return 0;
        }

        unitOfWorkDbContext.RemoveRange(devices);
        await WriteAuditSecurityLogAsync(
            action: "TrustedDevicesRevokedAll",
            extraProperties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["count"] = devices.Count
            });

        await unitOfWorkDbContext.SaveChangesAsync();
        return devices.Count;
    }

    private IQueryable<IdentityTrustedDevice> ApplyCurrentTenantScope(IQueryable<IdentityTrustedDevice> query)
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

    private async Task WriteAuditSecurityLogAsync(
        string action,
        Dictionary<string, object?>? extraProperties = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = _currentTenant.Id,
            ApplicationName = "Identity",
            Identity = "TrustedDevice",
            Action = action,
            UserId = _currentUser.Id,
            UserName = _currentUser.UserName,
            CreationTime = DateTime.UtcNow,
            ExtraProperties = extraProperties is { Count: > 0 }
                        ? JsonSerializer.Serialize(extraProperties)
                        : null
        });
    }

    private static string BuildDeviceKey(string? deviceHash)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
        {
            return "unknown";
        }

        var normalized = deviceHash.Trim();
        if (normalized.Length <= 12)
        {
            return normalized;
        }

        return normalized[..12];
    }
}
