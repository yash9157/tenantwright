using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Passkeys;
using Identity.Domain.Shared.Account;

namespace Auth.Clients;

public interface IIdentityAuthenticationClient
{
    Task<LoginResultDto> CheckPasswordAsync(
        string loginInput,
        string password,
        Guid? tenantId = null,
        InteractiveLoginMode mode = InteractiveLoginMode.Tenant,
        string? sessionId = null,
        string? clientId = null,
        string? clientIpAddress = null,
        string? browserInfo = null,
        string? correlationId = null,
        string? device = null,
        string? location = null,
        string? twoFactorCode = null,
        string? twoFactorRecoveryCode = null,
        string? deviceFingerprint = null,
        bool rememberTrustedDevice = false,
        CancellationToken cancellationToken = default);

    Task<InteractiveLoginResultDto> StartInteractiveLoginAsync(
        InteractiveLoginStartInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    Task<InteractiveLoginResultDto> CompleteMfaAsync(
        CompleteMfaInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    Task<InteractiveLoginResultDto> CompleteTrustDeviceAsync(
        CompleteTrustDeviceInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyEnrollmentAsync(
        InteractivePasskeyOptionsInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    Task<InteractiveLoginResultDto> CompleteInteractivePasskeyEnrollmentAsync(
        CompleteInteractivePasskeyInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyAssertionAsync(
        InteractivePasskeyOptionsInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    Task<InteractiveLoginResultDto> CompleteInteractivePasskeyAssertionAsync(
        CompleteInteractivePasskeyInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);
}
