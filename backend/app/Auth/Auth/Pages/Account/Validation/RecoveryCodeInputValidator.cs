using FluentValidation;

namespace Auth.Pages.Account.Validation;

public sealed class RecoveryCodeInputValidator : AbstractValidator<Auth.Pages.Account.RecoveryCodeModel.RecoveryCodeInput>
{
    public RecoveryCodeInputValidator()
    {
        RuleFor(model => model.RecoveryCode)
            .NotEmpty()
            .WithMessage("Recovery code is required.")
            .MinimumLength(6)
            .WithMessage("Enter a valid recovery code.")
            .Matches(@"^[A-Za-z0-9-]+$")
            .WithMessage("Enter a valid recovery code.");
    }
}
