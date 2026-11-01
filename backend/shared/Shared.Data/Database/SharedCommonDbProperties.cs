namespace Shared.Data.Database;

public static class SharedCommonDbProperties
{
    /// <summary>
    /// This table prefix is shared by most of the modules.
    /// You can change it to set table prefix for all modules using this.
    ///
    /// Default value: "App".
    /// </summary>
    public static string DbTablePrefix { get; set; } = "App";

    /// <summary>
    /// Default value: null.
    /// </summary>
    public static string? DbSchema { get; set; }
}
