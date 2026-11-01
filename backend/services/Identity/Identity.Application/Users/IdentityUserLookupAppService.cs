using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Users;
using Identity.Domain.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;

using Shared.AuditLogs.Auditing;
namespace Identity.Application.Users;

public class IdentityUserLookupAppService : IIdentityUserLookupAppService, ITransientDependency
{
    private readonly ApplicationUserManager _userManager;

    public IdentityUserLookupAppService(ApplicationUserManager userManager)
    {
        _userManager = userManager;
    }

    public async Task<UserData?> FindByIdAsync(Guid id)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());
        return user == null ? null : MapToUserData(user);
    }

    public async Task<UserData?> FindByUserNameAsync(string userName)
    {
        var user = await _userManager.FindByNameAsync(userName);
        return user == null ? null : MapToUserData(user);
    }

    public async Task<List<UserData>> SearchAsync(string filter, int maxResultCount = 10, int skipCount = 0)
    {
        var query = _userManager.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(u => (u.UserName ?? string.Empty).Contains(filter) || (u.Email ?? string.Empty).Contains(filter));
        }

        var users = await query.OrderBy(u => u.UserName)
                               .Skip(skipCount)
                               .Take(maxResultCount)
                               .ToListAsync();

        return users.Select(MapToUserData).ToList();
    }

    private UserData MapToUserData(ApplicationUser user)
    {
        return new UserData
        {
            Id = user.Id,
            UserName = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            Name = user.Name,
            Surname = user.Surname,
            TenantId = user.TenantId
        };
    }
}
