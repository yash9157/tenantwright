using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Auth.Pages.Account;

public static class PasswordChangedSuccessStateStore
{
    private const string TempDataKey = "Auth.PasswordChangedSuccessState";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static void Save(ITempDataDictionary tempData, PasswordChangedSuccessState state)
    {
        if (tempData == null || state == null)
        {
            return;
        }

        tempData[TempDataKey] = JsonSerializer.Serialize(state, SerializerOptions);
    }

    public static PasswordChangedSuccessState? Read(ITempDataDictionary tempData, bool keep = false)
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
            return JsonSerializer.Deserialize<PasswordChangedSuccessState>(serialized, SerializerOptions);
        }
        catch
        {
            tempData.Remove(TempDataKey);
            return null;
        }
    }
}
