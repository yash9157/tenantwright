using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account.Validation;

public static class PageModelValidationExtensions
{
    public static async Task<bool> TryValidateInputAsync<TModel>(
        this PageModel pageModel,
        IAuthPageValidationService validationService,
        TModel model,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        pageModel.TryValidateModel(model!, prefix);
        await validationService.ApplyAsync(pageModel.ModelState, model, prefix, cancellationToken);
        return pageModel.ModelState.IsValid;
    }
}
