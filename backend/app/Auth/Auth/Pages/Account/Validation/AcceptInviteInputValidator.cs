using FluentValidation;

namespace Auth.Pages.Account.Validation;

public sealed class AcceptInviteInputValidator : AbstractValidator<Auth.Pages.Account.AcceptInviteModel.AcceptInviteInput>
{
    public AcceptInviteInputValidator(IAuthPasswordPolicyProvider passwordPolicyProvider)
    {
        var policy = passwordPolicyProvider.GetPolicy();

        RuleFor(model => model.Token)
            .NotEmpty()
            .WithMessage("Invitation link is invalid.");

        RuleFor(model => model.Email)
            .NotEmpty()
            .WithMessage("Invitation email is required.")
            .EmailAddress()
            .WithMessage("Invitation email is invalid.");

        RuleFor(model => model.UserName)
            .NotEmpty()
            .WithMessage("Username is required.")
            .Matches("^[A-Za-z0-9._-]+$")
            .WithMessage("Enter a valid username.");

        RuleFor(model => model.Password)
            .NotEmpty()
            .WithMessage("Password is required.")
            .MinimumLength(policy.RequiredLength)
            .WithMessage($"Password must contain at least {policy.RequiredLength} characters.");

        if (policy.RequireLowercase)
        {
            RuleFor(model => model.Password)
                .Must(PasswordValidationRules.HasLowercase)
                .When(model => !string.IsNullOrWhiteSpace(model.Password))
                .WithMessage("Password must include a lowercase letter.");
        }

        if (policy.RequireUppercase)
        {
            RuleFor(model => model.Password)
                .Must(PasswordValidationRules.HasUppercase)
                .When(model => !string.IsNullOrWhiteSpace(model.Password))
                .WithMessage("Password must include an uppercase letter.");
        }

        if (policy.RequireDigit)
        {
            RuleFor(model => model.Password)
                .Must(PasswordValidationRules.HasDigit)
                .When(model => !string.IsNullOrWhiteSpace(model.Password))
                .WithMessage("Password must include a number.");
        }

        if (policy.RequireNonAlphanumeric)
        {
            RuleFor(model => model.Password)
                .Must(PasswordValidationRules.HasNonAlphanumeric)
                .When(model => !string.IsNullOrWhiteSpace(model.Password))
                .WithMessage("Password must include a symbol.");
        }

        RuleFor(model => model.ConfirmPassword)
            .NotEmpty()
            .WithMessage("Confirm your password.")
            .Equal(model => model.Password)
            .WithMessage("Passwords do not match.");
    }
}
