using Microsoft.Extensions.Hosting;
using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class MessagingOptionsAndLicenseTests
{
    [Fact]
    public void Disabled_options_should_not_require_runtime_secrets_or_broker_configuration()
    {
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment(Environments.Production));

        var result = validator.Validate(null, new MessagingOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Disabled_tenant_options_should_not_require_secrets_while_catalog_keeps_approved_authority()
    {
        var options = new MessagingOptions
        {
            Provider = TenantWrightMessagingProvider.None,
            ServiceName = TenantWrightMessagingTopology.TenantPrincipal,
            ProducerPrincipal = TenantWrightMessagingTopology.TenantPrincipal
        };
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment(Environments.Production));

        var result = validator.Validate(null, options);
        var catalog = TenantWrightMessagingTopology.CreateCatalog();

        Assert.True(result.Succeeded);
        Assert.Contains(
            TenantWrightMessagingTopology.TenantPrincipal,
            catalog.GetRoute(typeof(Shared.EventContracts.AuditLogWriteRequestedTenantEto)).AllowedOriginPrincipals);
        Assert.Equal(
            TenantWrightMessagingTopology.TenantPrincipal,
            catalog.GetEndpointRoute(
                TenantWrightMessagingTopology.PermissionCacheEndpoint(TenantWrightMessagingTopology.TenantPrincipal))
                .OwnerPrincipal);
    }

    [Fact]
    public void Enabled_options_should_accept_only_the_frozen_local_test_policy()
    {
        using var keys = new MessagingTestKeyMaterial();
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment("Testing"));

        var result = validator.Validate(null, MessagingTestData.CreateValidOptions(keys));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Fact]
    public void Enabled_options_should_reject_policy_drift_and_never_echo_secret_values()
    {
        const string secretMarker = "sensitive-test-marker-that-must-not-appear";
        using var keys = new MessagingTestKeyMaterial();
        var options = MessagingTestData.CreateValidOptions(keys);
        options.RabbitMq.Password = secretMarker;
        options.Delivery.ImmediateRetryMilliseconds = [251, 1000];
        options.RabbitMq.PublisherConfirmation = false;
        options.RabbitMq.DetectUnroutableMessages = false;
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment("Testing"));

        var result = validator.Validate(null, options);
        var failures = string.Join(Environment.NewLine, result.Failures ?? []);

        Assert.False(result.Succeeded);
        Assert.Contains("exactly [250, 1000]", failures, StringComparison.Ordinal);
        Assert.Contains("PublisherConfirmation", failures, StringComparison.Ordinal);
        Assert.Contains("DetectUnroutableMessages", failures, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMarker, failures, StringComparison.Ordinal);
    }

    [Fact]
    public void Insecure_transport_should_fail_closed_outside_local_or_test_environments()
    {
        using var keys = new MessagingTestKeyMaterial();
        var options = MessagingTestData.CreateValidOptions(keys);
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment(Environments.Production));

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("permitted only", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_tls_should_accept_only_a_read_only_ca_certificate()
    {
        using var keys = new MessagingTestKeyMaterial();
        using var certificates = new RabbitMqCertificateTestMaterial();
        var options = MessagingTestData.CreateValidOptions(keys);
        options.RabbitMq.Port = 5671;
        options.RabbitMq.UseTls = true;
        options.RabbitMq.AllowInsecureTransport = false;
        options.RabbitMq.TrustedCaCertificatePath = certificates.CaCertificatePath;
        options.RabbitMq.ClientCertificatePath = certificates.ClientCertificatePath;
        options.RabbitMq.ClientPrivateKeyPath = certificates.ClientPrivateKeyPath;
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment(Environments.Production));

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Fact]
    public void Production_tls_should_reject_a_writable_ca_file_without_disclosing_its_path()
    {
        using var keys = new MessagingTestKeyMaterial();
        using var certificates = new RabbitMqCertificateTestMaterial();
        certificates.MakeCaCertificateWritable();
        var options = MessagingTestData.CreateValidOptions(keys);
        options.RabbitMq.UseTls = true;
        options.RabbitMq.AllowInsecureTransport = false;
        options.RabbitMq.TrustedCaCertificatePath = certificates.CaCertificatePath;
        options.RabbitMq.ClientCertificatePath = certificates.ClientCertificatePath;
        options.RabbitMq.ClientPrivateKeyPath = certificates.ClientPrivateKeyPath;
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment(Environments.Production));

        var result = validator.Validate(null, options);
        var failures = string.Join(Environment.NewLine, result.Failures ?? []);

        Assert.False(result.Succeeded);
        Assert.Contains("TrustedCaCertificatePath", failures, StringComparison.Ordinal);
        Assert.DoesNotContain(certificates.CaCertificatePath, failures, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Custom_ca_validation_should_reject_name_mismatch_and_an_untrusted_server()
    {
        using var trusted = new RabbitMqCertificateTestMaterial();
        using var untrusted = new RabbitMqCertificateTestMaterial();
        using var presentedChain = new X509Chain();
        var callback = RabbitMqServerCertificateTrust.CreateValidationCallback(trusted.CaCertificatePath);

        Assert.True(callback(new object(), trusted.ServerCertificate, presentedChain, SslPolicyErrors.None));
        Assert.False(callback(
            new object(),
            trusted.ServerCertificate,
            presentedChain,
            SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(callback(
            new object(),
            untrusted.ServerCertificate,
            presentedChain,
            SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Writable_signing_key_should_fail_validation_without_disclosing_its_path()
    {
        using var keys = new MessagingTestKeyMaterial();
        keys.MakePrivateKeyWritable();
        var options = MessagingTestData.CreateValidOptions(keys);
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment("Testing"));

        var result = validator.Validate(null, options);
        var failures = string.Join(Environment.NewLine, result.Failures ?? []);

        Assert.False(result.Succeeded);
        Assert.Contains("Trust.PrivateKeyPath", failures, StringComparison.Ordinal);
        Assert.DoesNotContain(keys.PrivateKeyPath, failures, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void License_resolution_should_require_exactly_one_source()
    {
        var missing = new MassTransitLicenseResolver(_ => null);
        var both = new MassTransitLicenseResolver(name => name == MassTransitLicenseResolver.LicenseEnvironmentVariable
            ? "fixed-invalid-test-license"
            : "C:\\fixed-invalid-test-path");

        var missingException = Assert.Throws<InvalidOperationException>(() => missing.Resolve());
        var bothException = Assert.Throws<InvalidOperationException>(() => both.Resolve());

        Assert.Equal(missingException.Message, bothException.Message);
        Assert.DoesNotContain("fixed-invalid", bothException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_license_resolution_should_select_the_supported_interface_without_logging_material()
    {
        var resolver = new MassTransitLicenseResolver(name =>
            name == MassTransitLicenseResolver.LicenseEnvironmentVariable ? "fixed-invalid-test-license" : null);

        var source = resolver.Resolve();

        Assert.Equal(MassTransitLicenseSourceKind.Inline, source.Kind);
        Assert.False(string.IsNullOrWhiteSpace(source.Value));
    }

    [Fact]
    public void File_license_resolution_should_accept_only_an_absolute_read_only_regular_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tenantwright-license-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "license.txt");
        File.WriteAllText(path, "fixed-invalid-test-license");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            var resolver = new MassTransitLicenseResolver(name =>
                name == MassTransitLicenseResolver.LicensePathEnvironmentVariable ? path : null);

            var source = resolver.Resolve();

            Assert.Equal(MassTransitLicenseSourceKind.File, source.Kind);
            Assert.Equal(Path.GetFullPath(path), source.Value);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void License_path_errors_should_be_fixed_and_non_disclosing()
    {
        const string pathMarker = "license-path-marker-that-must-not-appear";
        var resolver = new MassTransitLicenseResolver(name =>
            name == MassTransitLicenseResolver.LicensePathEnvironmentVariable ? pathMarker : null);

        var exception = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());

        Assert.DoesNotContain(pathMarker, exception.Message, StringComparison.Ordinal);
        Assert.Contains("absolute", exception.Message, StringComparison.Ordinal);
    }
}

internal sealed class RabbitMqCertificateTestMaterial : IDisposable
{
    private readonly string _directory;
    private readonly X509Certificate2 _caCertificate;

    public RabbitMqCertificateTestMaterial()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tenantwright-rabbitmq-ca-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        CaCertificatePath = Path.Combine(_directory, "ca.crt");
        ClientCertificatePath = Path.Combine(_directory, "client.crt");
        ClientPrivateKeyPath = Path.Combine(_directory, "client.key");

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=TenantWright RabbitMQ Test CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        _caCertificate = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));

        File.WriteAllText(
            CaCertificatePath,
            PemEncoding.WriteString("CERTIFICATE", _caCertificate.RawData));
        File.SetAttributes(CaCertificatePath, File.GetAttributes(CaCertificatePath) | FileAttributes.ReadOnly);

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest(
            "CN=tenantwright-rabbitmq",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("tenantwright-rabbitmq");
        serverRequest.CertificateExtensions.Add(san.Build());
        ServerCertificate = serverRequest.Create(
            _caCertificate,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));

        using var clientKey = RSA.Create(2048);
        var clientRequest = new CertificateRequest(
            "CN=tenantwright-rabbitmq-client",
            clientKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        clientRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        clientRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            true));
        clientRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") },
            true));
        using var clientCertificate = clientRequest.Create(
            _caCertificate,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(
            ClientCertificatePath,
            PemEncoding.WriteString("CERTIFICATE", clientCertificate.RawData));
        File.WriteAllText(
            ClientPrivateKeyPath,
            clientKey.ExportPkcs8PrivateKeyPem());
        File.SetAttributes(ClientCertificatePath, File.GetAttributes(ClientCertificatePath) | FileAttributes.ReadOnly);
        File.SetAttributes(ClientPrivateKeyPath, File.GetAttributes(ClientPrivateKeyPath) | FileAttributes.ReadOnly);
    }

    public string CaCertificatePath { get; }
    public string ClientCertificatePath { get; }
    public string ClientPrivateKeyPath { get; }
    public X509Certificate2 ServerCertificate { get; }

    public void MakeCaCertificateWritable() =>
        File.SetAttributes(CaCertificatePath, File.GetAttributes(CaCertificatePath) & ~FileAttributes.ReadOnly);

    public void Dispose()
    {
        ServerCertificate.Dispose();
        _caCertificate.Dispose();
        foreach (var path in new[] { CaCertificatePath, ClientCertificatePath, ClientPrivateKeyPath })
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
