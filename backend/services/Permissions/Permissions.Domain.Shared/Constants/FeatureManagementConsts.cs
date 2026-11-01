namespace Permissions.Domain.Shared.Constants;

public static class FeatureValueConsts
{
    public static int MaxNameLength { get; set; } = 128;
    public static int MaxProviderNameLength { get; set; } = 64;
    public static int MaxProviderKeyLength { get; set; } = 64;
    public static int MaxValueLength { get; set; } = 128;
}

public static class FeatureDefinitionRecordConsts
{
    public static int MaxNameLength { get; set; } = 128;
    public static int MaxDisplayNameLength { get; set; } = 256;
    public static int MaxDescriptionLength { get; set; } = 256;
    public static int MaxDefaultValueLength { get; set; } = 256;
    public static int MaxAllowedProvidersLength { get; set; } = 256;
    public static int MaxValueTypeLength { get; set; } = 2048;

    /// <summary>Comma-separated permitted values for a <c>Selection</c> feature.</summary>
    public static int MaxAllowedValuesLength { get; set; } = 1024;

    /// <summary>Optional unit label shown next to a <c>Number</c> feature (e.g. "messages").</summary>
    public static int MaxUnitLength { get; set; } = 32;
}

public static class FeatureGroupDefinitionRecordConsts
{
    public static int MaxNameLength { get; set; } = 128;
    public static int MaxDisplayNameLength { get; set; } = 256;
}
