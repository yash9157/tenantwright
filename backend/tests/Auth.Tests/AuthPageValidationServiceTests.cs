using Auth.Pages.Account.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Auth.Tests;

public sealed class AuthPageValidationServiceTests
{
    [Fact]
    public async Task ApplyAsync_MapsValidatorFailuresIntoModelState()
    {
        var services = new ServiceCollection()
            .AddSingleton<IValidator<SampleInput>, SampleInputValidator>()
            .BuildServiceProvider();

        var validationService = new AuthPageValidationService(services);
        var modelState = new ModelStateDictionary();

        await validationService.ApplyAsync(
            modelState,
            new SampleInput { Value = string.Empty },
            "Input");

        Assert.False(modelState.IsValid);
        var propertyEntry = Assert.Single(modelState, entry => entry.Key == "Input.Value").Value;
        var globalEntry = Assert.Single(modelState, entry => entry.Key == string.Empty).Value;
        Assert.NotNull(propertyEntry);
        Assert.NotNull(globalEntry);
        Assert.Equal("Value is required.", Assert.Single(propertyEntry.Errors).ErrorMessage);
        Assert.Equal("Global validation failure.", Assert.Single(globalEntry.Errors).ErrorMessage);
    }

    [Fact]
    public async Task ApplyAsync_DoesNothingWhenNoValidatorIsRegistered()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var validationService = new AuthPageValidationService(services);
        var modelState = new ModelStateDictionary();

        await validationService.ApplyAsync(
            modelState,
            new SampleInput { Value = string.Empty },
            "Input");

        Assert.True(modelState.IsValid);
        Assert.Empty(modelState);
    }

    private sealed class SampleInput
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class SampleInputValidator : AbstractValidator<SampleInput>
    {
        public SampleInputValidator()
        {
            RuleFor(model => model.Value)
                .NotEmpty()
                .WithMessage("Value is required.");

            RuleFor(model => model)
                .Custom((_model, context) =>
                {
                    context.AddFailure(string.Empty, "Global validation failure.");
                });
        }
    }
}
