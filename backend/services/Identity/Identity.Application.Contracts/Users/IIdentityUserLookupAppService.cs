using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Identity.Application.Contracts.Users;

public class UserData
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public Guid? TenantId { get; set; }
}

public interface IIdentityUserLookupAppService
{
    Task<UserData?> FindByIdAsync(Guid id);
    Task<UserData?> FindByUserNameAsync(string userName);
    Task<List<UserData>> SearchAsync(string filter, int maxResultCount = 10, int skipCount = 0);
}
