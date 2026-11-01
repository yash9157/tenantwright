using System.ComponentModel.DataAnnotations;
using Identity.Application.Contracts.Account;
using Identity.Domain.Shared.Account;

namespace Identity.Application.Contracts.Passkeys;

public sealed class PasskeyOptions
{
    public const string SectionName = "Passkeys";

    public bool Enabled { get; set; } = true;

    [Required]
    [MaxLength(253)]
    public string RelyingPartyId { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string RelyingPartyName { get; set; } = "TenantWright";

    public HashSet<string> Origins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Range(60, 600)]
    public int CeremonyTtlSeconds { get; set; } = 300;
}

public sealed class PasskeyCredentialDto
{
    public Guid Id { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public Guid AaGuid { get; set; }
    public List<string> Transports { get; set; } = [];
    public bool IsBackupEligible { get; set; }
    public bool IsBackedUp { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
}

public sealed class PasskeyCeremonyOptionsDto
{
    public Guid CeremonyId { get; set; }
    public string OptionsJson { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class BeginPasskeyRegistrationInput
{
    [Required]
    [MaxLength(256)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string FriendlyName { get; set; } = string.Empty;
}

public sealed class CompletePasskeyRegistrationInput
{
    public Guid CeremonyId { get; set; }

    [Required]
    [MaxLength(65536)]
    public string ResponseJson { get; set; } = string.Empty;
}

public sealed class RenamePasskeyInput
{
    [Required]
    [MaxLength(128)]
    public string FriendlyName { get; set; } = string.Empty;
}

public sealed class RevokePasskeyInput
{
    [Required]
    [MaxLength(256)]
    public string CurrentPassword { get; set; } = string.Empty;
}

public sealed class InteractivePasskeyOptionsInput : InteractiveChallengeInput
{
}

public sealed class CompleteInteractivePasskeyInput : InteractiveChallengeInput
{
    public Guid CeremonyId { get; set; }

    [Required]
    [MaxLength(65536)]
    public string ResponseJson { get; set; } = string.Empty;
}

public interface IPasskeyAppService
{
    Task<List<PasskeyCredentialDto>> GetListAsync();
    Task<PasskeyCeremonyOptionsDto> BeginRegistrationAsync(BeginPasskeyRegistrationInput input);
    Task<PasskeyCredentialDto> CompleteRegistrationAsync(CompletePasskeyRegistrationInput input);
    Task<PasskeyCredentialDto> RenameAsync(Guid credentialId, RenamePasskeyInput input);
    Task RevokeAsync(Guid credentialId, RevokePasskeyInput input);
}
