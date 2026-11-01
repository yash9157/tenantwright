using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System.Text.Json;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.SecuritySettings;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.SecuritySettings;
using Identity.Domain.Shared.Account.Risk;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.SecuritySettings;

[ExposeServices(typeof(IHostIdentitySecuritySettingsAppService))]
public sealed class HostIdentitySecuritySettingsAppService : IHostIdentitySecuritySettingsAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly HostIdentitySecurityStaticOptions _staticOptions;
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;
    private readonly IHostIdentitySecurityPolicyProvider _policyProvider;

    public HostIdentitySecuritySettingsAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, ICurrentUser currentUser, HostIdentitySecurityStaticOptions staticOptions, HostIdentitySecurityPolicyRuntimeState runtimeState, IHostIdentitySecurityPolicyProvider policyProvider)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _staticOptions = staticOptions;
        _runtimeState = runtimeState;
        _policyProvider = policyProvider;
    }

    public Task<HostIdentitySecuritySettingsDto> GetHostAsync()
    {
        EnsureHostScope();
        return _policyProvider.GetEffectiveAsync();
    }

    [Audited]
    public async Task<HostIdentitySecuritySettingsDto> UpdateHostAsync(UpdateHostIdentitySecuritySettingsInput input)
    {
        return await UpdateHostAsync(input, changeReason: null);
    }

    public async Task<HostIdentitySecurityPolicyImpactDto> PreviewHostUpdateAsync(UpdateHostIdentitySecuritySettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHostScope();

        var requested = HostIdentitySecuritySettingsMapper.FromInput(input);
        ValidateRequestedSettings(requested);
        var existing = await GetExistingSettingsAsync();
        EnsureConcurrencyStamp(existing, input.ConcurrencyStamp);
        return BuildImpact(existing, requested, DateTime.UtcNow);
    }

    [Audited]
    public async Task<HostIdentitySecuritySettingsDto> UpdateHostGovernedAsync(GovernedHostIdentitySecuritySettingsUpdateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reason = input.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new SharedValidationException("A reason is required for a governed host security policy update.");
        }

        return await UpdateHostAsync(input.Settings, reason);
    }

    private async Task<HostIdentitySecuritySettingsDto> UpdateHostAsync(
        UpdateHostIdentitySecuritySettingsInput input,
        string? changeReason)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        ArgumentNullException.ThrowIfNull(input);
        EnsureHostScope();

        var requested = HostIdentitySecuritySettingsMapper.FromInput(input);
        ValidateRequestedSettings(requested);

        using var _ = _currentTenant.Change(null);
        var policy = await unitOfWorkDbContext.HostIdentitySecurityPolicies
            .FirstOrDefaultAsync(x => x.Id == HostIdentitySecurityPolicy.SingletonId);
        var existing = policy == null
            ? HostIdentitySecuritySettingsMapper.Clone(_staticOptions.DefaultSettings)
            : HostIdentitySecuritySettingsMapper.FromPolicy(policy);
        EnsureConcurrencyStamp(existing, input.ConcurrencyStamp);

        policy ??= HostIdentitySecuritySettingsMapper.CreatePolicy(existing);
        HostIdentitySecuritySettingsMapper.Apply(policy, requested);
        policy.ConcurrencyStamp = input.ConcurrencyStamp;

        if (unitOfWorkDbContext.IsDetached(policy))
        {
            await unitOfWorkDbContext.AddAsync(policy);
        }
        var impact = BuildImpact(existing, requested, DateTime.UtcNow);

        await unitOfWorkDbContext.AddAsync(new Domain.ApplicationUsers.IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = null,
            ApplicationName = "Identity",
            Identity = "SecuritySettings",
            Action = "HostSecurityPolicyUpdated",
            UserId = _currentUser.Id,
            UserName = _currentUser.UserName,
            CreationTime = DateTime.UtcNow,
            ExtraProperties = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["changedSections"] = impact.ChangedSections,
                ["effectiveAtUtc"] = impact.EffectiveAtUtc,
                ["affectsFutureSignIns"] = impact.AffectsFutureSignIns,
                ["activeSessionsRemainValid"] = impact.ActiveSessionsRemainValid,
                ["reason"] = changeReason
            })
        });

        try
        {
            await unitOfWorkDbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SharedValidationException("Host security settings were changed by another request. Reload and retry.");
        }

        var updated = HostIdentitySecuritySettingsMapper.FromPolicy(policy);
        await _policyProvider.InvalidateAsync();
        _runtimeState.SetCurrent(updated);
        return updated;
    }

    private async Task<HostIdentitySecuritySettingsDto> GetExistingSettingsAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        using var _ = _currentTenant.Change(null);
        var policy = await unitOfWorkDbContext.HostIdentitySecurityPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == HostIdentitySecurityPolicy.SingletonId);

        return policy == null
            ? HostIdentitySecuritySettingsMapper.Clone(_staticOptions.DefaultSettings)
            : HostIdentitySecuritySettingsMapper.FromPolicy(policy);
    }

    private static void EnsureConcurrencyStamp(HostIdentitySecuritySettingsDto existing, string? concurrencyStamp)
    {
        if (!string.Equals(existing.ConcurrencyStamp, concurrencyStamp, StringComparison.Ordinal))
        {
            throw new SharedValidationException("Host security settings were changed by another request. Reload and retry.");
        }
    }

    private void EnsureHostScope()
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Host identity security settings require host scope.");
        }

        if (!_currentUser.IsInRole(TenantWrightIdentityRoleNames.HostAdmin))
        {
            throw new UnauthorizedAccessException("Host identity security settings require host admin scope.");
        }
    }

    private static void ValidateRequestedSettings(HostIdentitySecuritySettingsDto settings)
    {
        if (settings.Password == null ||
            settings.Lockout == null ||
            settings.LoginRisk == null ||
            settings.InteractiveLogin == null ||
            settings.LoginAttemptProtection == null ||
            settings.PasswordReset == null ||
            settings.EmailVerification == null ||
            settings.PublicInvitationManagement == null)
        {
            throw new SharedValidationException("All host security policy sections are required.");
        }

        if (!Enum.IsDefined(settings.LoginRisk.Mode))
        {
            throw new SharedValidationException("Login risk mode is invalid.");
        }

        _ = HostIdentitySecuritySettingsMapper.CreatePolicy(settings);
    }

    private static List<string> GetChangedSections(HostIdentitySecuritySettingsDto before, HostIdentitySecuritySettingsDto after)
    {
        var changedSections = new List<string>();

        if (!SectionEquals(before.Password, after.Password))
        {
            changedSections.Add("Password");
        }

        if (!SectionEquals(before.Lockout, after.Lockout))
        {
            changedSections.Add("Lockout");
        }

        if (!SectionEquals(before.LoginRisk, after.LoginRisk))
        {
            changedSections.Add("LoginRisk");
        }

        if (!SectionEquals(before.InteractiveLogin, after.InteractiveLogin))
        {
            changedSections.Add("InteractiveLogin");
        }

        if (!SectionEquals(before.LoginAttemptProtection, after.LoginAttemptProtection))
        {
            changedSections.Add("LoginAttemptProtection");
        }

        if (!SectionEquals(before.PasswordReset, after.PasswordReset))
        {
            changedSections.Add("PasswordReset");
        }

        if (!SectionEquals(before.EmailVerification, after.EmailVerification))
        {
            changedSections.Add("EmailVerification");
        }

        if (!SectionEquals(before.PublicInvitationManagement, after.PublicInvitationManagement))
        {
            changedSections.Add("PublicInvitationManagement");
        }

        return changedSections;
    }

    private static HostIdentitySecurityPolicyImpactDto BuildImpact(
        HostIdentitySecuritySettingsDto before,
        HostIdentitySecuritySettingsDto after,
        DateTime effectiveAtUtc)
    {
        var changedSections = GetChangedSections(before, after);
        return new HostIdentitySecurityPolicyImpactDto
        {
            ChangedSections = changedSections,
            EffectiveAtUtc = effectiveAtUtc,
            AffectsFutureSignIns = changedSections.Count > 0,
            ActiveSessionsRemainValid = true
        };
    }

    private static bool SectionEquals<TSection>(TSection left, TSection right)
    {
        return JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    }
}
