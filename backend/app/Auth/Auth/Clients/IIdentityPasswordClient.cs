using Identity.Application.Contracts.Account;

namespace Auth.Clients;

public interface IIdentityPasswordClient
{
    Task RequestPasswordResetAsync(
        PasswordResetRequestInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<PasswordResetValidateResultDto> ValidatePasswordResetTokenAsync(
        PasswordResetValidateInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<PasswordResetCompleteResultDto> CompletePasswordResetAsync(
        PasswordResetCompleteInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<PasswordExpiredChangeResultDto> ChangeExpiredPasswordAsync(
        PasswordExpiredChangeInput input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    Task RequestEmailVerificationAsync(
        EmailVerificationRequestInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<EmailVerificationResultDto> ConfirmEmailVerificationAsync(
        EmailVerificationConfirmInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task ResendEmailVerificationAsync(
        EmailVerificationRequestInput input,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<PublicInvitationValidateResultDto> ValidatePublicInvitationAsync(
        PublicInvitationValidateInput input,
        CancellationToken cancellationToken = default);

    Task<PublicInvitationAcceptResultDto> AcceptPublicInvitationAsync(
        PublicInvitationAcceptInput input,
        CancellationToken cancellationToken = default);
}
