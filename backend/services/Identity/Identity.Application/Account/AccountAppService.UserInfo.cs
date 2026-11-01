using System.Threading.Tasks;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Caching;
using Identity.Application.Contracts.Users;
using Identity.Application.ApplicationUsers;
using Identity.Application.Caching;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Shared.Modularity.DependencyInjection;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System;
using Shared.Caching.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Identity.Application.Users;
using System.Security.Cryptography;
using System.Text;
using Shared;
using Shared.Caching.Sessions;
using Microsoft.Extensions.Options;
using Identity.Application.Account.Risk;
using Identity.Application.Contracts.Account.Risk;
using Identity.Domain.Shared.Account;
using Identity.Domain.Shared.Account.Risk;
using Shared.EventContracts;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.AuditLogs.Auditing;
using Shared.MultiTenancy;
using Identity.Application.SecurityLogs;
using Shared.Domain.Entities;
using Shared.Events;
namespace Identity.Application.Account;

public partial class AccountAppService
{
    public async Task<UserInfoDto?> GetUserInfoAsync(
        string loginInput,
        Guid? tenantId = null,
        InteractiveLoginMode mode = InteractiveLoginMode.Tenant)
    {
        if (!IsValidInteractiveContext(mode, tenantId))
        {
            return null;
        }

        var user = await FindInteractiveUserAsync(mode, tenantId, loginInput);
        if (user == null)
        {
            return null;
        }

        return await MapToUserInfoAsync(user, UserInfoSegments.Profile | UserInfoSegments.Authorization);
    }

    public async Task<UserInfoDto?> GetUserInfoByIdAsync(Guid userId, UserInfoSegments segments = UserInfoSegments.Profile | UserInfoSegments.Authorization)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var user = await _userManager.Users.FirstOrDefaultAsync(x => x.Id == userId);
        if (user == null)
        {
            return null;
        }

        return await MapToUserInfoAsync(user, segments);
    }
}
