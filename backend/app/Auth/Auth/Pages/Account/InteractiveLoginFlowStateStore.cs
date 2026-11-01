using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Auth.Pages.Account;

internal static class InteractiveLoginFlowStateStore
{
    private const string FlowStateKey = "Auth.InteractiveLogin.FlowState";

    public static void Save(ITempDataDictionary tempData, InteractiveLoginFlowState state)
    {
        tempData[FlowStateKey] = JsonSerializer.Serialize(state);
    }

    public static InteractiveLoginFlowState? Read(ITempDataDictionary tempData, bool keep = false)
    {
        if (!tempData.TryGetValue(FlowStateKey, out var raw))
        {
            return null;
        }

        if (raw is not string json || string.IsNullOrWhiteSpace(json))
        {
            tempData.Remove(FlowStateKey);
            return null;
        }

        if (keep)
        {
            tempData.Keep(FlowStateKey);
        }
        else
        {
            tempData.Remove(FlowStateKey);
        }

        return JsonSerializer.Deserialize<InteractiveLoginFlowState>(json);
    }
}
