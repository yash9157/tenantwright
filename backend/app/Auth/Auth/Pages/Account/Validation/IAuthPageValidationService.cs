using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Auth.Pages.Account.Validation;

public interface IAuthPageValidationService
{
    Task ApplyAsync<TModel>(
        ModelStateDictionary modelState,
        TModel model,
        string prefix,
        CancellationToken cancellationToken = default);
}
