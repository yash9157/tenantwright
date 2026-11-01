using System.ComponentModel.DataAnnotations;

namespace Identity.Application.Contracts.Users;

/// <summary>
/// The only fields a caller needs to recognise a colleague and select them.
/// Deliberately far narrower than <see cref="UserDto"/>, which also carries phone number,
/// lock state, two-factor state, email-confirmation state, roles, and lifecycle status —
/// security posture about a person that no picker needs in order to fill a field.
/// </summary>
public class UserDirectoryEntryDto
{
    public Guid Id { get; set; }

    /// <summary>Full name where one is recorded; otherwise the user name.</summary>
    public string DisplayName { get; set; } = default!;

    public string Email { get; set; } = default!;
}

/// <summary>
/// A search, never a listing. An empty or short query returns nothing rather than the
/// directory, so this surface cannot be used to enumerate a tenant's users.
/// </summary>
public class SearchUserDirectoryInput
{
    /// <summary>
    /// Matched against user name, email, and recorded names. The minimum length is enforced
    /// in the application service as well, so a caller reaching the service directly cannot
    /// bypass it by omitting model validation.
    /// </summary>
    [Required]
    [MinLength(UserDirectoryConsts.MinQueryLength)]
    [MaxLength(UserDirectoryConsts.MaxQueryLength)]
    public string Query { get; set; } = default!;

    /// <summary>Clamped to <see cref="UserDirectoryConsts.MaxResultCount"/> by the service.</summary>
    [Range(1, UserDirectoryConsts.MaxResultCount)]
    public int MaxResultCount { get; set; } = UserDirectoryConsts.DefaultResultCount;
}

public static class UserDirectoryConsts
{
    /// <summary>Below this length the service returns an empty result instead of querying.</summary>
    public const int MinQueryLength = 3;

    public const int MaxQueryLength = 128;

    /// <summary>
    /// A hard cap, not a page size. There is no paging: a caller who cannot find someone
    /// narrows the query instead of walking the directory.
    /// </summary>
    public const int MaxResultCount = 25;

    public const int DefaultResultCount = 10;
}

public interface IUserDirectoryAppService
{
    Task<IReadOnlyList<UserDirectoryEntryDto>> SearchAsync(
        SearchUserDirectoryInput input,
        CancellationToken cancellationToken = default);
}
