using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Identity.Application.Contracts.Users;

public class IdentityLinkUserDto
{
    public Guid Id { get; set; }
    public Guid SourceUserId { get; set; }
    public Guid? SourceTenantId { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid? TargetTenantId { get; set; }
    public Guid CounterpartUserId { get; set; }
    public string? CounterpartUserName { get; set; }
    public string? CounterpartEmail { get; set; }
}

public class LinkUserDto
{
    public Guid TargetUserId { get; set; }
    public Guid? TargetTenantId { get; set; }
}

public class CreateLinkUserRequestInput
{
    public Guid TargetUserId { get; set; }
}

public class CreateLinkUserRequestForUserInput
{
    public Guid SourceUserId { get; set; }
    public Guid TargetUserId { get; set; }
}

public class LinkUserRequestAdminDecisionInput
{
    public Guid UserId { get; set; }
}

public class RejectLinkUserRequestForUserInput : LinkUserRequestAdminDecisionInput
{
    public string? Reason { get; set; }
}

public class LinkUserAdminUnlinkInput
{
    public Guid SourceUserId { get; set; }
    public Guid TargetUserId { get; set; }
}

public class RejectLinkUserRequestInput
{
    public string? Reason { get; set; }
}

public class IdentityLinkUserRequestDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string? RequestedByUserName { get; set; }
    public string? RequestedByEmail { get; set; }
    public Guid TargetUserId { get; set; }
    public string? TargetUserName { get; set; }
    public string? TargetEmail { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RespondedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
    public bool IsIncoming { get; set; }
}

public interface IIdentityLinkUserAppService
{
    Task<List<IdentityLinkUserDto>> GetListAsync();
    Task<List<IdentityLinkUserDto>> GetListForUserAsync(Guid userId);
    Task<List<IdentityLinkUserRequestDto>> GetRequestsAsync();
    Task<List<IdentityLinkUserRequestDto>> GetRequestsForUserAsync(Guid userId);
    Task CreateRequestAsync(CreateLinkUserRequestInput input);
    Task CreateRequestForUserAsync(CreateLinkUserRequestForUserInput input);
    Task ApproveRequestAsync(Guid id);
    Task ApproveRequestForUserAsync(Guid id, LinkUserRequestAdminDecisionInput input);
    Task RejectRequestAsync(Guid id, RejectLinkUserRequestInput? input = null);
    Task RejectRequestForUserAsync(Guid id, RejectLinkUserRequestForUserInput input);
    Task LinkAsync(LinkUserDto input);
    Task UnlinkAsync(LinkUserDto input);
    Task UnlinkForUserAsync(LinkUserAdminUnlinkInput input);
}
