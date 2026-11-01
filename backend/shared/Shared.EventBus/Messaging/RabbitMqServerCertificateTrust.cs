using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Shared.EventBus.Messaging;

/// <summary>
/// Pins the RabbitMQ server certificate chain to the configured <see cref="MessagingRabbitMqOptions.TrustedCaCertificatePath"/>.
/// Provider-neutral: every RabbitMQ-backed provider uses this callback for its TLS connection.
/// </summary>
public static class RabbitMqServerCertificateTrust
{
    public static RemoteCertificateValidationCallback CreateValidationCallback(string configuredPath)
    {
        var path = MessagingKeyFile.Validate(configuredPath, "RabbitMQ trusted CA certificate");
        var trustedRoot = X509CertificateLoader.LoadCertificateFromFile(path);
        var basicConstraints = trustedRoot.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        if (basicConstraints is null || !basicConstraints.CertificateAuthority)
        {
            trustedRoot.Dispose();
            throw new InvalidOperationException("The RabbitMQ trusted CA certificate must identify a certificate authority.");
        }

        return (_, certificate, presentedChain, policyErrors) =>
            Validate(trustedRoot, certificate, presentedChain, policyErrors);
    }

    private static bool Validate(
        X509Certificate2 trustedRoot,
        X509Certificate? certificate,
        X509Chain? presentedChain,
        SslPolicyErrors policyErrors)
    {
        if (certificate is null ||
            (policyErrors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                             SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var validationChain = new X509Chain();
        validationChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        validationChain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        validationChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        validationChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        if (presentedChain is not null)
        {
            foreach (var element in presentedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
            {
                if (!string.Equals(
                        element.Certificate.Thumbprint,
                        trustedRoot.Thumbprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    validationChain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }
        }

        return validationChain.Build(leaf) &&
               validationChain.ChainElements.Count > 0 &&
               string.Equals(
                   validationChain.ChainElements[^1].Certificate.Thumbprint,
                   trustedRoot.Thumbprint,
                   StringComparison.OrdinalIgnoreCase);
    }
}
