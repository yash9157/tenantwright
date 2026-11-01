using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Application.Contracts.Account;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Account;

public sealed class InternalProfilePreferenceLookupAppService : IInternalProfilePreferenceLookupAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;

    public InternalProfilePreferenceLookupAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
    }

    public async Task<UserSecurityAlertPreferenceStateDto> GetAlertPreferenceAsync(Guid userId, string alertKey)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (userId == Guid.Empty)
        {
            throw new SharedValidationException("User id is required.");
        }

        var normalizedAlertKey = alertKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedAlertKey) || !KnownAlertKeys.Contains(normalizedAlertKey))
        {
            throw new SharedValidationException("Alert key is invalid.");
        }

        var user = await unitOfWorkDbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.Id, x.TenantId })
            .FirstOrDefaultAsync();
        if (user == null)
        {
            throw new SharedValidationException("User not found.");
        }

        if (user.TenantId != _currentTenant.Id)
        {
            throw new SharedValidationException("User is outside the current tenant scope.");
        }

        var preferences = await unitOfWorkDbContext.IdentityUserNotificationSecurityPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.TenantId == user.TenantId);

        return new UserSecurityAlertPreferenceStateDto
        {
            UserId = userId,
            TenantId = user.TenantId,
            AlertKey = normalizedAlertKey,
            Enabled = ResolveAlertPreference(preferences, normalizedAlertKey)
        };
    }

    private static readonly HashSet<string> KnownAlertKeys =
    [
        IdentityUserNotificationAlertKeys.NewDeviceSignIn,
        IdentityUserNotificationAlertKeys.RiskBlocked,
        IdentityUserNotificationAlertKeys.AdminSessionRevoked,
        IdentityUserNotificationAlertKeys.MfaChanged
    ];

    private static bool ResolveAlertPreference(
        IdentityUserNotificationSecurityPreference? preferences,
        string alertKey)
    {
        if (preferences == null)
        {
            return true;
        }

        if (!preferences.EmailEnabled)
        {
            return false;
        }

        return alertKey switch
        {
            IdentityUserNotificationAlertKeys.NewDeviceSignIn => preferences.NewDeviceSignInAlertEnabled,
            IdentityUserNotificationAlertKeys.RiskBlocked => preferences.RiskBlockedAlertEnabled,
            IdentityUserNotificationAlertKeys.AdminSessionRevoked => preferences.AdminSessionRevokedAlertEnabled,
            IdentityUserNotificationAlertKeys.MfaChanged => preferences.MfaChangedAlertEnabled,
            _ => false
        };
    }
}
