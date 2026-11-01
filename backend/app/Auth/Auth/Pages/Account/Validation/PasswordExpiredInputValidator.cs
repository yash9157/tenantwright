using FluentValidation;

namespace Auth.Pages.Account.Validation;

public sealed class PasswordExpiredInputValidator : AbstractValidator<Auth.Pages.Account.PasswordExpiredModel.PasswordExpiredInput>
{
    public PasswordExpiredInputValidator(IAuthPasswordPolicyProvider passwordPolicyProvider)
    {
        var policy = passwordPolicyProvider.GetPolicy();

        RuleFor(model => model.CurrentPassword)
            .NotEmpty()
            .WithMessage("Enter your current password.");

        RuleFor(model => model.NewPassword)
            .NotEmpty()
            .WithMessage("Password is required.")
            .MinimumLength(policy.RequiredLength)
            .WithMessage($"Password must contain at least {policy.RequiredLength} characters.");

        if (policy.RequireLowercase)
        {
            RuleFor(model => model.NewPassword)
                .Must(PasswordValidationRules.HasLowercase)
                .When(model => !string.IsNullOrWhiteSpace(model.NewPassword))
                .WithMessage("Password must include a lowercase letter.");
        }

        if (policy.RequireUppercase)
        {
            RuleFor(model => model.NewPassword)
                .Must(PasswordValidationRules.HasUppercase)
                .When(model => !string.IsNullOrWhiteSpace(model.NewPassword))
                .WithMessage("Password must include an uppercase letter.");
        }

        if (policy.RequireDigit)
        {
            RuleFor(model => model.NewPassword)
                .Must(PasswordValidationRules.HasDigit)
                .When(model => !string.IsNullOrWhiteSpace(model.NewPassword))
                .WithMessage("Password must include a number.");
        }

        if (policy.RequireNonAlphanumeric)
        {
            RuleFor(model => model.NewPassword)
                .Must(PasswordValidationRules.HasNonAlphanumeric)
                .When(model => !string.IsNullOrWhiteSpace(model.NewPassword))
                .WithMessage("Password must include a symbol.");
        }

        RuleFor(model => model.ConfirmPassword)
            .NotEmpty()
            .WithMessage("Confirm your new password.")
            .Equal(model => model.NewPassword)
            .WithMessage("Passwords do not match.");
    }
}
