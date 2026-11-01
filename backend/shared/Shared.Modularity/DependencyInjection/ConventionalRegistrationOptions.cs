namespace Shared.Modularity.DependencyInjection;

public sealed class ConventionalRegistrationOptions
{
    public const string SectionName = "Shared:DependencyInjection";

    public ConventionalServiceExposureMode ExposureMode { get; set; } =
        ConventionalServiceExposureMode.Default;

    public bool FailOnLegacyExposureMode { get; set; } = true;

    public bool FailOnDuplicateRegistrations { get; set; } = true;
}
