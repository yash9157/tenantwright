using Auth.Pages.Account;
using Auth.Pages.Account.Validation;
using FluentValidation;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Auth.Tests;

public sealed class AuthInputValidatorsTests
{
    [Fact]
    public async Task MfaValidator_RequiresAuthenticatorCodeOrRecoveryCode()
    {
        var validator = new MfaInputModelValidator();

        var result = await validator.ValidateAsync(new MfaModel.MfaInputModel());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Enter an authenticator code or a recovery code.");
    }

    [Fact]
    public async Task MfaValidator_AcceptsValidAuthenticatorCode()
    {
        var validator = new MfaInputModelValidator();

        var result = await validator.ValidateAsync(new MfaModel.MfaInputModel
        {
            TwoFactorCode = "123456"
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MfaInputModel_DoesNotTrustDeviceByDefault()
    {
        var input = new MfaModel.MfaInputModel();

        Assert.False(input.RememberTrustedDevice);
    }

    [Fact]
    public async Task ResetPasswordValidator_RejectsInvalidLinkAndPasswordMismatch()
    {
        var validator = CreateResetPasswordValidator("Production");

        var result = await validator.ValidateAsync(new ResetPasswordModel.ResetPasswordInput
        {
            TenantId = Guid.Empty,
            UserId = Guid.Empty,
            Token = string.Empty,
            NewPassword = "weakpass",
            ConfirmPassword = "different"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Reset link is invalid.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Password must include an uppercase letter.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Password must include a number.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Password must include a symbol.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Passwords do not match.");
    }

    [Fact]
    public async Task ResetPasswordValidator_AcceptsStrongPasswordWhenPolicyIsSatisfied()
    {
        var validator = CreateResetPasswordValidator("Production");

        var result = await validator.ValidateAsync(new ResetPasswordModel.ResetPasswordInput
        {
            TenantId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Token = "reset-token",
            NewPassword = "StrongPass1!",
            ConfirmPassword = "StrongPass1!"
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task PasswordExpiredValidator_RejectsMissingCurrentPasswordAndWeakPassword()
    {
        var validator = CreatePasswordExpiredValidator("Production");

        var result = await validator.ValidateAsync(new PasswordExpiredModel.PasswordExpiredInput
        {
            CurrentPassword = string.Empty,
            NewPassword = "short",
            ConfirmPassword = "short"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Enter your current password.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Password must contain at least 8 characters.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Password must include an uppercase letter.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Password must include a number.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Password must include a symbol.");
    }

    [Fact]
    public async Task RecoveryCodeValidator_RejectsInvalidFormat()
    {
        var validator = new RecoveryCodeInputValidator();

        var result = await validator.ValidateAsync(new RecoveryCodeModel.RecoveryCodeInput
        {
            RecoveryCode = "bad code!"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Enter a valid recovery code.");
    }

    [Fact]
    public async Task RecoveryCodeValidator_AcceptsNormalizedFormat()
    {
        var validator = new RecoveryCodeInputValidator();

        var result = await validator.ValidateAsync(new RecoveryCodeModel.RecoveryCodeInput
        {
            RecoveryCode = "A1B2-C3D4"
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RecoveryCodeInput_DoesNotTrustDeviceByDefault()
    {
        var input = new RecoveryCodeModel.RecoveryCodeInput();

        Assert.False(input.RememberTrustedDevice);
    }

    [Fact]
    public async Task CreateAccountValidator_RejectsInvalidOptionalUsernameAndEmail()
    {
        var validator = CreateCreateAccountValidator("Production");

        var result = await validator.ValidateAsync(new CreateAccountModel.CreateAccountInput
        {
            Tenant = "tenant with spaces",
            UserName = "bad user",
            Email = "bad-email"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "UserName");
        Assert.Contains(result.Errors, error => error.PropertyName == "Email");
    }

    [Fact]
    public async Task AcceptInviteValidator_RequiresValidEmailUsernameAndStrongPassword()
    {
        var validator = CreateAcceptInviteValidator("Production");

        var result = await validator.ValidateAsync(new AcceptInviteModel.AcceptInviteInput
        {
            Token = string.Empty,
            Email = "invite",
            UserName = "bad user",
            Password = "short",
            ConfirmPassword = "different"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "Token");
        Assert.Contains(result.Errors, error => error.PropertyName == "Email");
        Assert.Contains(result.Errors, error => error.PropertyName == "UserName");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Password must contain at least 8 characters.");
        Assert.Contains(result.Errors, error => error.ErrorMessage == "Passwords do not match.");
    }

    private static ResetPasswordInputValidator CreateResetPasswordValidator(string environmentName)
    {
        return new ResetPasswordInputValidator(new AuthPasswordPolicyProvider(new FakeHostEnvironment(environmentName)));
    }

    private static PasswordExpiredInputValidator CreatePasswordExpiredValidator(string environmentName)
    {
        return new PasswordExpiredInputValidator(new AuthPasswordPolicyProvider(new FakeHostEnvironment(environmentName)));
    }

    private static CreateAccountInputValidator CreateCreateAccountValidator(string environmentName)
    {
        return new CreateAccountInputValidator();
    }

    private static AcceptInviteInputValidator CreateAcceptInviteValidator(string environmentName)
    {
        return new AcceptInviteInputValidator(new AuthPasswordPolicyProvider(new FakeHostEnvironment(environmentName)));
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "Auth.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
