using System.Security.Cryptography;
using Aspire.Hosting.Publishing;

namespace TenantWright.AppHost;

/// <summary>
/// AppHost secret parameters. Each value is generated on first run and persisted to the AppHost
/// user secrets, so a fresh clone starts without manual setup. Existing user-secret values always
/// win. Generated bootstrap passwords are visible on the Aspire dashboard parameter view and in
/// <c>dotnet user-secrets list --project orchestration/TenantWright.AppHost</c>.
/// </summary>
internal static class LocalSecrets
{
    public static IResourceBuilder<ParameterResource> AddPassword(IDistributedApplicationBuilder builder, string name)
        => builder.AddParameter(
            name,
            new GenerateParameterDefault
            {
                MinLength = 24,
                MinLower = 2,
                MinUpper = 2,
                MinNumeric = 2,
                MinSpecial = 2
            },
            secret: true,
            persist: true);

    public static IResourceBuilder<ParameterResource> AddClientSecret(IDistributedApplicationBuilder builder, string name)
        => builder.AddParameter(
            name,
            new GenerateParameterDefault { MinLength = 48, Special = false },
            secret: true,
            persist: true);

    /// <summary>Adds a base64-encoded 256-bit symmetric key, as required by AES-256-GCM protectors.</summary>
    public static IResourceBuilder<ParameterResource> AddAes256Key(IDistributedApplicationBuilder builder, string name)
        => builder.AddParameter(name, new Aes256KeyParameterDefault(), secret: true, persist: true);

    private sealed class Aes256KeyParameterDefault : ParameterDefault
    {
        public override string GetDefaultValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        public override void WriteToManifest(ManifestPublishingContext context)
        {
            // Local-only parameter: deployed environments supply this key through their own secret store.
        }
    }
}
