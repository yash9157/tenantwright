using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Identity.Application.Contracts.Users;

public class IdentityUserDelegationDto
{
    public Guid Id { get; set; }
    public Guid SourceUserId { get; set; }
    public string? SourceUserName { get; set; }
    public string? SourceEmail { get; set; }
    public Guid TargetUserId { get; set; }
    public string? TargetUserName { get; set; }
    public string? TargetEmail { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool IsActive { get; set; }
    public bool IsExpired { get; set; }
}

public class CreateUserDelegationDto
{
    public Guid TargetUserId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

public class CreateUserDelegationForSourceDto : CreateUserDelegationDto
{
    public Guid SourceUserId { get; set; }
}

public interface IIdentityUserDelegationAppService
{
    Task<List<IdentityUserDelegationDto>> GetListAsync();
    Task<List<IdentityUserDelegationDto>> GetListForSourceAsync(Guid sourceUserId);
    Task<List<IdentityUserDelegationDto>> GetActiveDelegationsAsync();
    Task<List<IdentityUserDelegationDto>> GetActiveDelegationsForTargetAsync(Guid targetUserId);
    Task DelegateNewUserAsync(CreateUserDelegationDto input);
    Task DelegateNewUserForSourceAsync(CreateUserDelegationForSourceDto input);
    Task DeleteDelegationAsync(Guid id);
    Task DeleteDelegationForSourceAsync(Guid id, Guid sourceUserId);
}
