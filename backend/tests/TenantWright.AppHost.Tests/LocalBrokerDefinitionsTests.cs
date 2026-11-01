using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace TenantWright.AppHost.Tests;

public sealed class LocalBrokerDefinitionsTests : IDisposable
{
    private const string AdminUser = "local-admin";
    private const string VirtualHost = "tenantwright-development-wolverine";

    private static readonly string[] Principals =
    [
        "tenantwright-auditlogs", "tenantwright-auth", "tenantwright-identity", "tenantwright-notifications",
        "tenantwright-permissions", "tenantwright-tenant", "tenantwright-workflows"
    ];

    private readonly TemporaryDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Definitions_should_declare_every_principal_and_the_administrator_with_verifiable_sha256_hashes()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        var files = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);

        using var document = JsonDocument.Parse(File.ReadAllBytes(files.DefinitionsPath));
        var users = document.RootElement.GetProperty("users").EnumerateArray().ToDictionary(
            user => user.GetProperty("name").GetString()!,
            user => user,
            StringComparer.Ordinal);
        Assert.Equal(Principals.Append(AdminUser).Order(StringComparer.Ordinal), users.Keys.Order(StringComparer.Ordinal));

        var admin = users[AdminUser];
        Assert.True(RabbitMqPasswordHash.Verify(material.BrokerAdminPassword, admin.GetProperty("password_hash").GetString()));
        Assert.Equal(["administrator"], admin.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));
        foreach (var principal in material.Principals)
        {
            var user = users[principal.Principal];
            Assert.Equal("rabbit_password_hashing_sha256", user.GetProperty("hashing_algorithm").GetString());
            Assert.True(RabbitMqPasswordHash.Verify(principal.BrokerPassword, user.GetProperty("password_hash").GetString()));
            Assert.Empty(user.GetProperty("tags").EnumerateArray());
        }

        var text = File.ReadAllText(files.DefinitionsPath);
        Assert.DoesNotContain(material.BrokerAdminPassword, text, StringComparison.Ordinal);
        Assert.All(material.Principals, principal => Assert.DoesNotContain(principal.BrokerPassword, text, StringComparison.Ordinal));
    }

    [Fact]
    public void Definitions_should_grant_each_principal_the_chart_permission_model_on_its_own_vhost_only()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var chart = ReadChartPermissions();

        var files = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);

        using var document = JsonDocument.Parse(File.ReadAllBytes(files.DefinitionsPath));
        Assert.Equal(
            ["/", VirtualHost],
            document.RootElement.GetProperty("vhosts").EnumerateArray().Select(vhost => vhost.GetProperty("name").GetString()));

        var permissions = document.RootElement.GetProperty("permissions").EnumerateArray().ToArray();
        foreach (var principal in Principals)
        {
            var grant = Assert.Single(permissions, permission => permission.GetProperty("user").GetString() == principal);
            Assert.Equal(VirtualHost, grant.GetProperty("vhost").GetString());
            Assert.Equal(chart.Configure, grant.GetProperty("configure").GetString());
            Assert.Equal(chart.Write, grant.GetProperty("write").GetString());
            Assert.Equal($"^({principal}(?:-|$).*|{chart.ReadShared})$", grant.GetProperty("read").GetString());
        }

        var adminGrants = permissions.Where(permission => permission.GetProperty("user").GetString() == AdminUser).ToArray();
        Assert.Equal(["/", VirtualHost], adminGrants.Select(grant => grant.GetProperty("vhost").GetString()).Order(StringComparer.Ordinal));
        Assert.All(adminGrants, grant => Assert.Equal(".*", grant.GetProperty("read").GetString()));
        Assert.Equal(Principals.Length + 2, permissions.Length);

        var policy = Assert.Single(document.RootElement.GetProperty("policies").EnumerateArray());
        Assert.Equal(VirtualHost, policy.GetProperty("vhost").GetString());
        Assert.Equal("tenantwright-quorum-guardrails", policy.GetProperty("name").GetString());
        Assert.Equal("^tenantwright-", policy.GetProperty("pattern").GetString());
    }

    [Theory]
    [InlineData("tenantwright-auth", "tenantwright-auth-permission-cache-v1", true)]
    [InlineData("tenantwright-auth", "tenantwright-auth", true)]
    [InlineData("tenantwright-auth", "tenantwright-auditlogs-write-tenant-v1", false)]
    [InlineData("tenantwright-auth", "tenantwright-authx-queue", false)]
    [InlineData("tenantwright-auth", "Shared.EventContracts.TenantLifecycleChangedEto", true)]
    [InlineData("tenantwright-auditlogs", "tenantwright-auditlogs-write-tenant-v1-dead-letter", true)]
    [InlineData("tenantwright-auditlogs", "tenantwright-auditlogs-tenant-database-changed-v1", true)]
    [InlineData("tenantwright-notifications", "tenantwright-notifications-tenant-database-changed-v1", true)]
    [InlineData("tenantwright-permissions", "tenantwright-permissions-tenant-database-changed-v1", true)]
    [InlineData("tenantwright-permissions", "tenantwright-notifications-tenant-database-changed-v1", false)]
    [InlineData("tenantwright-notifications", "Shared.EventContracts.TenantDatabaseChangedEto", true)]
    public void Read_permission_should_be_confined_to_the_principals_own_objects_and_shared_exchanges(
        string principal,
        string resource,
        bool allowed)
    {
        Assert.Equal(allowed, Regex.IsMatch(resource, LocalBrokerDefinitions.ReadPattern(principal), RegexOptions.CultureInvariant));
    }

    [Fact]
    public void Ensure_should_be_byte_stable_while_the_material_is_unchanged()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var first = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);
        var definitions = File.ReadAllBytes(first.DefinitionsPath);
        var writtenAt = File.GetLastWriteTimeUtc(first.DefinitionsPath);

        var second = LocalBrokerDefinitions.Ensure(LocalMessagingMaterialStore.Ensure(_root.Path, Principals), AdminUser, VirtualHost);

        Assert.Equal(first, second);
        Assert.Equal(definitions, File.ReadAllBytes(second.DefinitionsPath));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(second.DefinitionsPath));
        Assert.Matches("^[0-9a-f]{64}$", second.Fingerprint);
    }

    [Fact]
    public void Ensure_should_rehash_a_user_whose_stored_hash_no_longer_verifies()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var first = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);
        var original = File.ReadAllText(first.DefinitionsPath);
        var authHash = HashOf(original, "tenantwright-auth");
        File.WriteAllText(first.DefinitionsPath, original.Replace(authHash, RabbitMqPasswordHash.Create("rotated-elsewhere"), StringComparison.Ordinal));

        var second = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);

        var regenerated = File.ReadAllText(second.DefinitionsPath);
        Assert.True(RabbitMqPasswordHash.Verify(material.For("tenantwright-auth").BrokerPassword, HashOf(regenerated, "tenantwright-auth")));
        Assert.Equal(HashOf(original, "tenantwright-tenant"), HashOf(regenerated, "tenantwright-tenant"));
    }

    [Fact]
    public void Ensure_should_regenerate_an_unreadable_definitions_file()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var first = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);
        File.WriteAllText(first.DefinitionsPath, "{ not json");

        var second = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);

        using var document = JsonDocument.Parse(File.ReadAllBytes(second.DefinitionsPath));
        Assert.Equal(Principals.Length + 1, document.RootElement.GetProperty("users").GetArrayLength());
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Ensure_should_change_the_fingerprint_when_the_vhost_changes()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        var wolverine = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);
        var massTransit = LocalBrokerDefinitions.Ensure(material, AdminUser, "tenantwright-development-masstransit");

        Assert.NotEqual(wolverine.Fingerprint, massTransit.Fingerprint);
    }

    [Fact]
    public void Configuration_should_import_the_mounted_definitions_on_boot()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        var files = LocalBrokerDefinitions.Ensure(material, AdminUser, VirtualHost);

        var configuration = File.ReadAllText(files.ConfigurationPath);
        Assert.Contains("definitions.import_backend = local_filesystem\n", configuration, StringComparison.Ordinal);
        Assert.Contains($"definitions.local.path = {LocalBrokerDefinitions.ContainerDefinitionsPath}\n", configuration, StringComparison.Ordinal);
        Assert.Contains("default_queue_type = quorum\n", configuration, StringComparison.Ordinal);
        Assert.StartsWith("/etc/rabbitmq/conf.d/", LocalBrokerDefinitions.ContainerConfigurationPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_should_reject_the_default_vhost_and_an_administrator_named_like_a_principal()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        Assert.Throws<InvalidOperationException>(() => LocalBrokerDefinitions.Ensure(material, AdminUser, "/"));
        Assert.Throws<InvalidOperationException>(() => LocalBrokerDefinitions.Ensure(material, "tenantwright-auth", VirtualHost));
    }

    private static string HashOf(string definitions, string user)
    {
        using var document = JsonDocument.Parse(definitions);
        return document.RootElement.GetProperty("users").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == user)
            .GetProperty("password_hash").GetString()!;
    }

    /// <summary>The permission patterns from <c>charts/tenantwright-rabbitmq/values.yaml</c>, the deployed model.</summary>
    private static (string Configure, string Write, string ReadShared) ReadChartPermissions()
    {
        var values = File.ReadAllText(Path.Combine(RepositoryFiles.Root, "charts", "tenantwright-rabbitmq", "values.yaml"));
        return (Read("configurePattern"), Read("writePattern"), Read("readSharedPattern"));

        string Read(string key)
        {
            var match = Regex.Match(values, $"^\\s*{key}:\\s*'(?<value>[^']+)'\\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            Assert.True(match.Success, $"charts/tenantwright-rabbitmq/values.yaml must declare {key}.");
            return match.Groups["value"].Value;
        }
    }
}
