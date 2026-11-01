using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Domain.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;

namespace Identity.Application.ApplicationUsers;

public class ApplicationRoleManager : RoleManager<ApplicationRole>, IScopedDependency
{
    public ApplicationRoleManager(
        IRoleStore<ApplicationRole> store,
        IEnumerable<IRoleValidator<ApplicationRole>> roleValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        ILogger<ApplicationRoleManager> logger)
        : base(store, roleValidators, keyNormalizer, errors, logger)
    {
    }

    public virtual async Task<ApplicationRole> GetByIdAsync(Guid id)
    {
        var role = await FindByIdAsync(id.ToString());
        if (role == null) throw new EntityNotFoundException<ApplicationRole>(id);
        return role;
    }
}
