using FluentValidation;

namespace Auth.Pages.Account.Validation;

public sealed class MfaInputModelValidator : AbstractValidator<Auth.Pages.Account.MfaModel.MfaInputModel>
{
    public MfaInputModelValidator()
    {
        RuleFor(model => model)
            .Must(model =>
                !string.IsNullOrWhiteSpace(model.TwoFactorCode) ||
                !string.IsNullOrWhiteSpace(model.RecoveryCode))
            .WithMessage("Enter an authenticator code or a recovery code.");

        RuleFor(model => model.TwoFactorCode)
            .Matches(@"^\d{6,8}$")
            .When(model => !string.IsNullOrWhiteSpace(model.TwoFactorCode))
            .WithMessage("Enter the complete 6-digit code.");

        RuleFor(model => model.RecoveryCode)
            .Matches(@"^[A-Za-z0-9-]+$")
            .When(model => !string.IsNullOrWhiteSpace(model.RecoveryCode))
            .WithMessage("Enter a valid recovery code.");
    }
}
