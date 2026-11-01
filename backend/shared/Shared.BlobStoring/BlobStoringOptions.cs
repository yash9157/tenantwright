namespace Shared.BlobStoring;

public sealed class BlobStoringOptions
{
    public const string SectionName = "BlobStoring";

    public BlobContainerConfigurations Containers { get; } = new();

    public bool FailWhenTenantMissingForMultiTenantContainers { get; set; } = true;

    public bool EnableTelemetry { get; set; } = true;
}
