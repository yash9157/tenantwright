using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Auth.Pages.Account;

public static class CreateAccountSuccessStateStore
{
    private const string TempDataKey = "Auth.CreateAccountSuccessState";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static void Save(ITempDataDictionary tempData, CreateAccountSuccessState state)
    {
        if (tempData == null || state == null)
        {
            return;
        }

        tempData[TempDataKey] = JsonSerializer.Serialize(state, SerializerOptions);
    }

    public static CreateAccountSuccessState? Read(ITempDataDictionary tempData, bool keep = false)
    {
        if (tempData == null)
        {
            return null;
        }

        if (!tempData.TryGetValue(TempDataKey, out var value) || value is not string serialized || string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        if (keep)
        {
            tempData.Keep(TempDataKey);
        }

        try
        {
            return JsonSerializer.Deserialize<CreateAccountSuccessState>(serialized, SerializerOptions);
        }
        catch
        {
            tempData.Remove(TempDataKey);
            return null;
        }
    }
}

public sealed class CreateAccountSuccessState
{
    public string Flow { get; set; } = "bootstrap";
    public string Tenant { get; set; } = string.Empty;
    public string LoginInput { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
