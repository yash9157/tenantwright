namespace Shared.Data.ConnectionStrings;

public class SharedConnectionStringCheckResult
{
    public bool IsValid { get; set; }

    public bool Connected { get; set; }

    public bool DatabaseExists { get; set; }
}
