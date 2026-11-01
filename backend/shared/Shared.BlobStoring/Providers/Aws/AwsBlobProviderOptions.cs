namespace Shared.BlobStoring.Providers.Aws;

public sealed class AwsBlobProviderOptions
{
    private readonly BlobContainerConfiguration _containerConfiguration;

    public AwsBlobProviderOptions(BlobContainerConfiguration containerConfiguration)
    {
        _containerConfiguration = containerConfiguration;
    }

    public string? AccessKeyId
    {
        get => _containerConfiguration.GetConfigurationOrDefault<string>(AwsBlobProviderConfigurationNames.AccessKeyId);
        set => _containerConfiguration.SetConfiguration(AwsBlobProviderConfigurationNames.AccessKeyId, value);
    }

    public string? SecretAccessKey
    {
        get => _containerConfiguration.GetConfigurationOrDefault<string>(AwsBlobProviderConfigurationNames.SecretAccessKey);
        set => _containerConfiguration.SetConfiguration(AwsBlobProviderConfigurationNames.SecretAccessKey, value);
    }

    public string Region
    {
        get => _containerConfiguration.GetConfigurationOrDefault(
                AwsBlobProviderConfigurationNames.Region,
                string.Empty) ?? string.Empty;
        set => _containerConfiguration.SetConfiguration(
            AwsBlobProviderConfigurationNames.Region,
            string.IsNullOrWhiteSpace(value)
                ? throw new BlobConfigurationException("AWS Region cannot be empty.")
                : value);
    }

    public string? ContainerName
    {
        get => _containerConfiguration.GetConfigurationOrDefault<string>(AwsBlobProviderConfigurationNames.ContainerName);
        set => _containerConfiguration.SetConfiguration(AwsBlobProviderConfigurationNames.ContainerName, value);
    }

    public bool CreateContainerIfNotExists
    {
        get => _containerConfiguration.GetConfigurationOrDefault(
            AwsBlobProviderConfigurationNames.CreateContainerIfNotExists,
            false);
        set => _containerConfiguration.SetConfiguration(AwsBlobProviderConfigurationNames.CreateContainerIfNotExists, value);
    }

    public string? ServiceUrl
    {
        get => _containerConfiguration.GetConfigurationOrDefault<string>(AwsBlobProviderConfigurationNames.ServiceUrl);
        set => _containerConfiguration.SetConfiguration(AwsBlobProviderConfigurationNames.ServiceUrl, value);
    }

    public bool UseSsl
    {
        get => _containerConfiguration.GetConfigurationOrDefault(AwsBlobProviderConfigurationNames.UseSsl, true);
        set => _containerConfiguration.SetConfiguration(AwsBlobProviderConfigurationNames.UseSsl, value);
    }

    public bool ForcePathStyle
    {
        get => _containerConfiguration.GetConfigurationOrDefault(AwsBlobProviderConfigurationNames.ForcePathStyle, false);
        set => _containerConfiguration.SetConfiguration(AwsBlobProviderConfigurationNames.ForcePathStyle, value);
    }

    public bool UseDefaultCredentials
    {
        get => _containerConfiguration.GetConfigurationOrDefault(AwsBlobProviderConfigurationNames.UseDefaultCredentials, true);
        set => _containerConfiguration.SetConfiguration(AwsBlobProviderConfigurationNames.UseDefaultCredentials, value);
    }
}
