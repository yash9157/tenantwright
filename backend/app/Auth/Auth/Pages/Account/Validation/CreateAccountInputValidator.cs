using FluentValidation;

namespace Auth.Pages.Account.Validation;

public sealed class CreateAccountInputValidator : AbstractValidator<Auth.Pages.Account.CreateAccountModel.CreateAccountInput>
{
    public CreateAccountInputValidator()
    {
        RuleFor(model => model.IdempotencyToken)
            .Matches("^[a-fA-F0-9]{32}$")
            .WithMessage("Registration session is invalid. Reload the page and try again.");

        RuleFor(model => model.UserName)
            .MaximumLength(AuthInputConstraints.LoginInputMaxLength)
            .Matches("^[A-Za-z0-9._-]+$")
            .When(model => !string.IsNullOrWhiteSpace(model.UserName))
            .WithMessage("Enter a valid username.");

        RuleFor(model => model.Email)
            .NotEmpty()
            .WithMessage("Email is required.")
            .EmailAddress()
            .WithMessage("Enter a valid work email.");

    }
}
