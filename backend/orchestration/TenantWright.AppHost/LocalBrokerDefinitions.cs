using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TenantWright.AppHost;

/// <summary>The generated broker files the local RabbitMQ container mounts, plus their content fingerprint.</summary>
/// <param name="DefinitionsPath">Host path of the definitions document.</param>
/// <param name="ConfigurationPath">Host path of the <c>conf.d</c> fragment that imports it on boot.</param>
/// <param name="Fingerprint">SHA-256 over both files; changes whenever the broker must re-import.</param>
internal sealed record LocalBrokerFiles(string DefinitionsPath, string ConfigurationPath, string Fingerprint);

/// <summary>
/// Declarative provisioning for the local RabbitMQ broker: a definitions document (users with salted SHA-256
/// password hashes, vhosts, permissions, the queue guardrail policy) that the node imports on every boot through
/// <c>definitions.import_backend = local_filesystem</c>.
/// </summary>
/// <remarks>
/// <para>
/// Boot import is an idempotent upsert, so a persistent container converges on the generated state every time it
/// starts and the provisioning cannot drift from the material. A blank node that imports definitions skips the
/// image's default user, so the administrator the AppHost health check and dashboard use is declared here too.
/// </para>
/// <para>
/// Principal permissions are the <c>charts/tenantwright-rabbitmq</c> model: configure and write <c>tenantwright-*</c>
/// names, the shared event exchanges and MassTransit's delay infrastructure; read only the principal's own
/// <c>{principal}-*</c> objects plus the shared exchanges. The administrator gets full rights on both vhosts.
/// </para>
/// <para>
/// The document is regenerated deterministically: an existing user hash is kept while it still verifies against
/// the current password, so an unchanged material directory yields byte-identical files and an unchanged
/// fingerprint (the fingerprint is part of the container's configuration, so any change recreates the container
/// and the node re-imports).
/// </para>
/// </remarks>
internal static class LocalBrokerDefinitions
{
    public const string ContainerDefinitionsPath = "/etc/rabbitmq/tenantwright-local-definitions.json";
    public const string ContainerConfigurationPath = "/etc/rabbitmq/conf.d/90-tenantwright-local.conf";
    public const string DefaultVirtualHost = "/";
    public const string ConfigurePattern = "^(tenantwright-.*|mt-delay-.*|mt-delay-return|Shared.EventContracts.*|.*Eto)$";
    public const string WritePattern = ConfigurePattern;
    public const string ReadSharedPattern = "mt-delay-.*|Shared.EventContracts.*|.*Eto";
    public const string QueuePolicyName = "tenantwright-quorum-guardrails";

    private const string DirectoryName = "rabbitmq";
    private const string DefinitionsFileName = "definitions.json";
    private const string ConfigurationFileName = "tenantwright-local.conf";
    private const string AdministratorTag = "administrator";
    private const string FullAccessPattern = ".*";

    public static string ReadPattern(string principal) => $"^({principal}(?:-|$).*|{ReadSharedPattern})$";

    public static LocalBrokerFiles Ensure(LocalMessagingMaterial material, string adminUserName, string virtualHost)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualHost);
        if (string.Equals(virtualHost, DefaultVirtualHost, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Local messaging must use an isolated vhost, not '/'.");
        }

        if (material.Principals.Any(principal => string.Equals(principal.Principal, adminUserName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The broker administrator cannot share a name with a messaging principal.");
        }

        var directory = Path.Combine(material.RootDirectory, DirectoryName);
        LocalSecretFile.EnsureDirectory(directory);
        var definitionsPath = Path.Combine(directory, DefinitionsFileName);
        var configurationPath = Path.Combine(directory, ConfigurationFileName);

        var existingHashes = ReadExistingHashes(definitionsPath);
        var definitions = BuildDefinitions(material, adminUserName, virtualHost, existingHashes);
        var configuration = Encoding.UTF8.GetBytes(BuildConfiguration());
        LocalSecretFile.WriteIfChanged(definitionsPath, definitions);
        LocalSecretFile.WriteIfChanged(configurationPath, configuration);

        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(definitions);
        fingerprint.AppendData([0]);
        fingerprint.AppendData(configuration);
        return new LocalBrokerFiles(definitionsPath, configurationPath, Convert.ToHexStringLower(fingerprint.GetHashAndReset()));
    }

    public static string BuildConfiguration()
        => string.Join(
            '\n',
            "# Generated by TenantWright.AppHost for local development; regenerated on every AppHost start.",
            "definitions.import_backend = local_filesystem",
            $"definitions.local.path = {ContainerDefinitionsPath}",
            "default_queue_type = quorum",
            "consumer_timeout = 1800000",
            string.Empty);

    private static byte[] BuildDefinitions(
        LocalMessagingMaterial material,
        string adminUserName,
        string virtualHost,
        IReadOnlyDictionary<string, string> existingHashes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();

            writer.WriteStartArray("users");
            WriteUser(writer, adminUserName, material.BrokerAdminPassword, AdministratorTag, existingHashes);
            foreach (var principal in material.Principals)
            {
                WriteUser(writer, principal.Principal, principal.BrokerPassword, tag: null, existingHashes);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("vhosts");
            WriteVirtualHost(writer, DefaultVirtualHost);
            WriteVirtualHost(writer, virtualHost);
            writer.WriteEndArray();

            // The administrator's '/' permission, which the AppHost health check (and so every waiting service)
            // depends on, is deliberately listed after the principals' entries.
            writer.WriteStartArray("permissions");
            foreach (var principal in material.Principals)
            {
                WritePermission(writer, principal.Principal, virtualHost, ConfigurePattern, WritePattern, ReadPattern(principal.Principal));
            }

            WritePermission(writer, adminUserName, virtualHost, FullAccessPattern, FullAccessPattern, FullAccessPattern);
            WritePermission(writer, adminUserName, DefaultVirtualHost, FullAccessPattern, FullAccessPattern, FullAccessPattern);
            writer.WriteEndArray();

            writer.WriteStartArray("policies");
            WriteQueuePolicy(writer, virtualHost);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static void WriteUser(
        Utf8JsonWriter writer,
        string name,
        string password,
        string? tag,
        IReadOnlyDictionary<string, string> existingHashes)
    {
        var hash = existingHashes.TryGetValue(name, out var existing) && RabbitMqPasswordHash.Verify(password, existing)
            ? existing
            : RabbitMqPasswordHash.Create(password);

        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("password_hash", hash);
        writer.WriteString("hashing_algorithm", RabbitMqPasswordHash.HashingAlgorithm);
        writer.WriteStartArray("tags");
        if (tag is not null)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteVirtualHost(Utf8JsonWriter writer, string name)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteEndObject();
    }

    private static void WritePermission(
        Utf8JsonWriter writer,
        string user,
        string virtualHost,
        string configure,
        string write,
        string read)
    {
        writer.WriteStartObject();
        writer.WriteString("user", user);
        writer.WriteString("vhost", virtualHost);
        writer.WriteString("configure", configure);
        writer.WriteString("write", write);
        writer.WriteString("read", read);
        writer.WriteEndObject();
    }

    /// <summary>The chart's <c>tenantwright-quorum-guardrails</c> policy.</summary>
    private static void WriteQueuePolicy(Utf8JsonWriter writer, string virtualHost)
    {
        writer.WriteStartObject();
        writer.WriteString("vhost", virtualHost);
        writer.WriteString("name", QueuePolicyName);
        writer.WriteString("pattern", "^tenantwright-");
        writer.WriteString("apply-to", "queues");
        writer.WriteNumber("priority", 100);
        writer.WriteStartObject("definition");
        writer.WriteNumber("delivery-limit", 20);
        writer.WriteNumber("max-length-bytes", 1073741824);
        writer.WriteString("overflow", "reject-publish");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// The user hashes of a previous generation. The file is derived output, so an unreadable one is simply
    /// regenerated: the password files remain the authoritative material.
    /// </summary>
    private static Dictionary<string, string> ReadExistingHashes(string definitionsPath)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(definitionsPath))
        {
            return hashes;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(definitionsPath));
            if (!document.RootElement.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
            {
                return hashes;
            }

            foreach (var user in users.EnumerateArray())
            {
                if (user.ValueKind == JsonValueKind.Object &&
                    user.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    user.TryGetProperty("password_hash", out var hash) && hash.ValueKind == JsonValueKind.String &&
                    user.TryGetProperty("hashing_algorithm", out var algorithm) &&
                    algorithm.ValueKind == JsonValueKind.String &&
                    string.Equals(algorithm.GetString(), RabbitMqPasswordHash.HashingAlgorithm, StringComparison.Ordinal))
                {
                    hashes[name.GetString()!] = hash.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            hashes.Clear();
        }

        return hashes;
    }
}
