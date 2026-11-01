namespace Shared.Permissions.Features;

public class FeatureCheckerClientOptions
{
    public bool Enabled { get; set; }
    public bool FailOpen { get; set; }
    public string BaseAddress { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 2;
    public string Scope { get; set; } = "features.check";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
