using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Shared.Caching;

public static class ValkeyConnectionConfiguration
{
    public const string SectionName = "Caching:Valkey";

    public static ConfigurationOptions Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var endpointConfiguration = configuration[$"{SectionName}:Configuration"];
        if (string.IsNullOrWhiteSpace(endpointConfiguration))
        {
            throw new InvalidOperationException($"{SectionName}:Configuration is required.");
        }

        var options = ConfigurationOptions.Parse(endpointConfiguration, ignoreUnknown: false);
        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Configuration must not contain a password. Supply it through {SectionName}:Password.");
        }

        var user = configuration[$"{SectionName}:User"];
        if (!string.IsNullOrWhiteSpace(user))
        {
            options.User = user.Trim();
        }

        var password = configuration[$"{SectionName}:Password"];
        if (!string.IsNullOrWhiteSpace(password))
        {
            options.Password = password;
        }

        var trustedCaPath = configuration[$"{SectionName}:TrustedCaCertificatePath"];
        if (string.IsNullOrWhiteSpace(trustedCaPath))
        {
            return options;
        }

        if (!options.Ssl)
        {
            throw new InvalidOperationException(
                $"{SectionName}:TrustedCaCertificatePath requires TLS in {SectionName}:Configuration.");
        }

        var resolvedTrustedCaPath = Path.GetFullPath(trustedCaPath);
        if (!File.Exists(resolvedTrustedCaPath))
        {
            throw new InvalidOperationException(
                $"The configured Valkey trusted CA certificate file does not exist: {resolvedTrustedCaPath}");
        }

        var trustedRoot = X509CertificateLoader.LoadCertificateFromFile(resolvedTrustedCaPath);
        options.CheckCertificateRevocation = false;
        options.CertificateValidation += (_, certificate, _, policyErrors) =>
            ValidateServerCertificate(certificate, policyErrors, trustedRoot);

        return options;
    }

    private static bool ValidateServerCertificate(
        X509Certificate? certificate,
        SslPolicyErrors policyErrors,
        X509Certificate2 trustedRoot)
    {
        if (certificate is null ||
            policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) ||
            policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        using var serverCertificate =
            X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(serverCertificate);
    }
}
