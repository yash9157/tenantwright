using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Passkeys;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Passkeys;

[Authorize]
public sealed class PasskeyAppService : IPasskeyAppService, ITransientDependency
{
    private readonly PasskeyCeremonyService _ceremonyService;
    private readonly ApplicationUserManager _userManager;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentUser _currentUser;

    public PasskeyAppService(
        PasskeyCeremonyService ceremonyService, ApplicationUserManager userManager, IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentUser currentUser)
    {
        _ceremonyService = ceremonyService;
        _userManager = userManager;
        _dbContextProvider = dbContextProvider;
        _currentUser = currentUser;
    }

    public async Task<List<PasskeyCredentialDto>> GetListAsync()
    {
        var user = await GetCurrentUserAsync();
        return (await _ceremonyService.ListUserCredentialsAsync(user.Id, NormalizeTenantId(user)))
            .OrderByDescending(x => x.LastUsedAtUtc ?? x.CreatedAtUtc)
            .Select(x => PasskeyCeremonyService.ToDto(x))
            .ToList();
    }

    [DisableAuditing]
    public async Task<PasskeyCeremonyOptionsDto> BeginRegistrationAsync(BeginPasskeyRegistrationInput input)
    {
        var user = await GetCurrentUserAsync();
        if (!await _userManager.CheckPasswordAsync(user, input.CurrentPassword))
        {
            PasskeyMetrics.Record("register", "password_rejected");
            throw new SharedValidationException("Current password is invalid.");
        }

        return await _ceremonyService.BeginRegistrationAsync(
            user,
            input.FriendlyName,
            PasskeyCeremonyPurpose.SelfServiceRegistration,
            interactiveChallengeId: null);
    }

    [DisableAuditing]
    public async Task<PasskeyCredentialDto> CompleteRegistrationAsync(CompletePasskeyRegistrationInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await GetCurrentUserAsync();
        var credential = await _ceremonyService.CompleteRegistrationAsync(
            user,
            input.CeremonyId,
            input.ResponseJson,
            PasskeyCeremonyPurpose.SelfServiceRegistration,
            interactiveChallengeId: null);
        await unitOfWorkDbContext.IdentityPasskeyCredentials.AddAsync(credential);
        await AddSecurityLogAsync(user, "PasskeyRegistered");
        await unitOfWorkDbContext.SaveChangesAsync();
        PasskeyMetrics.Record("register", "success");
        return PasskeyCeremonyService.ToDto(credential);
    }

    public async Task<PasskeyCredentialDto> RenameAsync(Guid credentialId, RenamePasskeyInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await GetCurrentUserAsync();
        var credential = await FindOwnedCredentialAsync(user, credentialId);
        credential.FriendlyName = PasskeyCeremonyService.NormalizeFriendlyName(input.FriendlyName);
        credential.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await AddSecurityLogAsync(user, "PasskeyRenamed");
        await unitOfWorkDbContext.SaveChangesAsync();
        PasskeyMetrics.Record("rename", "success");
        return PasskeyCeremonyService.ToDto(credential);
    }

    [DisableAuditing]
    public async Task RevokeAsync(Guid credentialId, RevokePasskeyInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await GetCurrentUserAsync();
        if (!await _userManager.CheckPasswordAsync(user, input.CurrentPassword))
        {
            PasskeyMetrics.Record("revoke", "password_rejected");
            throw new SharedValidationException("Current password is invalid.");
        }

        var credential = await FindOwnedCredentialAsync(user, credentialId);
        var credentialCount = await _ceremonyService.CountUserCredentialsAsync(user.Id, NormalizeTenantId(user));
        if (credentialCount <= 1 && string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            throw new SharedValidationException("The last usable authentication credential cannot be removed.");
        }

        unitOfWorkDbContext.IdentityPasskeyCredentials.Remove(credential);
        await AddSecurityLogAsync(user, "PasskeyRevoked");
        await unitOfWorkDbContext.SaveChangesAsync();
        PasskeyMetrics.Record("revoke", "success");
    }

    private async Task<ApplicationUser> GetCurrentUserAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (!_currentUser.Id.HasValue)
        {
            throw new UnauthorizedAccessException("An authenticated user is required.");
        }

        var user = await unitOfWorkDbContext.Users.SingleOrDefaultAsync(x => x.Id == _currentUser.Id.Value)
                   ?? throw new UnauthorizedAccessException("The current user no longer exists.");
        if (NormalizeTenantId(user) != _currentUser.TenantId)
        {
            throw new UnauthorizedAccessException("The current user tenant does not match the request context.");
        }

        return user;
    }

    private async Task<IdentityPasskeyCredential> FindOwnedCredentialAsync(ApplicationUser user, Guid credentialId)
    {
        return await _ceremonyService.FindUserCredentialAsync(user.Id, NormalizeTenantId(user), credentialId)
               ?? throw new SharedValidationException("Passkey was not found.");
    }

    private async Task AddSecurityLogAsync(ApplicationUser user, string action)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        await unitOfWorkDbContext.IdentitySecurityLogs.AddAsync(new IdentitySecurityLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = NormalizeTenantId(user),
            ApplicationName = "Identity",
            Identity = "Passkey",
            Action = action,
            UserId = user.Id,
            UserName = user.UserName,
            CreationTime = DateTime.UtcNow
        });
    }

    private static Guid? NormalizeTenantId(ApplicationUser user)
        => user.TenantId == Guid.Empty ? null : user.TenantId;
}
