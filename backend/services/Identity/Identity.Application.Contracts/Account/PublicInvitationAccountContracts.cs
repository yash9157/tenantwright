using System.ComponentModel.DataAnnotations;

namespace Identity.Application.Contracts.Account;

public class PublicInvitationValidateInput
{
    [Required]
    [MaxLength(2048)]
    public string Token { get; set; } = string.Empty;
}

public class PublicInvitationValidateResultDto
{
    public bool Succeeded { get; set; }
    public bool IsExpired { get; set; }
    public bool IsUsed { get; set; }
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? RoleName { get; set; }
    public string? SuggestedUserName { get; set; }
    public string? Message { get; set; }
}

public class PublicInvitationAcceptInput
{
    [Required]
    [MaxLength(2048)]
    public string Token { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string UserName { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? PhoneNumber { get; set; }

    [Required]
    [MaxLength(256)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string VerificationBaseUrl { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? ClientIpAddress { get; set; }

    [MaxLength(1024)]
    public string? BrowserInfo { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }
}

public class PublicInvitationAcceptResultDto
{
    public bool Succeeded { get; set; }
    public bool IsExpired { get; set; }
    public bool IsUsed { get; set; }
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = [];
}

public sealed class OwnershipInvitationAcceptInput
{
    [Required]
    [MaxLength(2048)]
    public string Token { get; set; } = string.Empty;
}
