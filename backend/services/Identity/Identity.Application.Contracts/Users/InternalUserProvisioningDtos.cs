using System.ComponentModel.DataAnnotations;

namespace Identity.Application.Contracts.Users;

public class ProvisionFirstUserInput
{
    [Required]
    [StringLength(64, MinimumLength = 2)]
    [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Role name format is invalid.")]
    public string RoleName { get; set; } = "TenantAdmin";

    [Required]
    [StringLength(256, MinimumLength = 3)]
    public string UserName { get; set; } = default!;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = default!;

    [Required]
    [StringLength(256, MinimumLength = 8)]
    public string Password { get; set; } = default!;

    [StringLength(32)]
    public string? PhoneNumber { get; set; }

    public bool IsActive { get; set; } = true;
    public bool RequireEmailActivationBeforeLogin { get; set; }
}

public class ProvisionFirstUserResultDto
{
    public bool Succeeded { get; set; }

    public bool TenantAlreadyInitialized { get; set; }

    public string? UserId { get; set; }

    public string? Error { get; set; }
}
