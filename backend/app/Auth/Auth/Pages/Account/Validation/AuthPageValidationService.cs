using FluentValidation;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.Pages.Account.Validation;

public sealed class AuthPageValidationService : IAuthPageValidationService
{
    private readonly IServiceProvider _serviceProvider;

    public AuthPageValidationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task ApplyAsync<TModel>(
        ModelStateDictionary modelState,
        TModel model,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        var validator = _serviceProvider.GetService<IValidator<TModel>>();
        if (validator == null)
        {
            return;
        }

        var result = await validator.ValidateAsync(model, cancellationToken);
        foreach (var failure in result.Errors)
        {
            if (string.IsNullOrWhiteSpace(failure.ErrorMessage))
            {
                continue;
            }

            modelState.AddModelError(BuildModelStateKey(prefix, failure.PropertyName), failure.ErrorMessage);
        }
    }

    private static string BuildModelStateKey(string prefix, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(prefix)
            ? propertyName
            : $"{prefix}.{propertyName}";
    }
}
