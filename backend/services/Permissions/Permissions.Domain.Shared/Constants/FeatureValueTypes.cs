namespace Permissions.Domain.Shared.Constants;

/// <summary>
/// Supported feature-definition value types. The type drives both the editor control and
/// server-side validation of a submitted value. Only <see cref="Toggle"/> is seeded today; the
/// others are recognized so typed features can be introduced without another contract change.
/// </summary>
public static class FeatureValueTypes
{
    public const string Toggle = "Toggle";
    public const string Number = "Number";
    public const string FreeText = "FreeText";
    public const string Selection = "Selection";

    public static bool IsKnown(string valueType) =>
        valueType is Toggle or Number or FreeText or Selection;
}
