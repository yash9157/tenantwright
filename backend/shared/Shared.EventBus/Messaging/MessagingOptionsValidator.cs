using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Shared.EventBus.Messaging;

public sealed class MessagingOptionsValidator(IHostEnvironment environment) : IValidateOptions<MessagingOptions>
{
    private static readonly int[] RequiredImmediateRetryMilliseconds = [250, 1000];
    private static readonly int[] RequiredDelayedRedeliverySeconds = [30, 120, 600];

    public ValidateOptionsResult Validate(string? name, MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(options.Provider))
        {
            return ValidateOptionsResult.Fail(
                $"Provider must be one of: {string.Join(", ", Enum.GetNames<TenantWrightMessagingProvider>())}.");
        }

        // A bus-free host has no connection, keys or delivery policy to validate. Every real provider shares the
        // neutral broker identity, RabbitMQ connection, delivery, trust and replay contract below; anything only one
        // provider reads is validated by that provider's own registration.
        if (options.Provider == TenantWrightMessagingProvider.None)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        ValidatePrincipal(options.ServiceName, nameof(options.ServiceName), failures);
        ValidatePrincipal(options.ProducerPrincipal, nameof(options.ProducerPrincipal), failures);

        if (!string.Equals(options.ServiceName, options.ProducerPrincipal, StringComparison.Ordinal))
        {
            failures.Add("ServiceName and ProducerPrincipal must be the same exact broker identity.");
        }

        if (options.RabbitMq is null)
        {
            failures.Add("RabbitMq configuration is required.");
        }
        else
        {
            ValidateRabbitMq(options.RabbitMq, options.ProducerPrincipal, failures);
        }

        if (options.Delivery is null)
        {
            failures.Add("Delivery configuration is required.");
        }
        else
        {
            ValidateDelivery(options.Delivery, failures);
        }

        if (options.Trust is null)
        {
            failures.Add("Trust configuration is required.");
        }
        else
        {
            ValidateTrust(options.Trust, options.ProducerPrincipal, failures);
        }

        if (options.Replay is null)
        {
            failures.Add("Replay configuration is required.");
        }
        else
        {
            ValidateReplay(options.Replay, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateRabbitMq(
        MessagingRabbitMqOptions options,
        string producerPrincipal,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.HostName))
        {
            failures.Add("RabbitMq.HostName is required.");
        }

        if (options.Port is < 1 or > 65535)
        {
            failures.Add("RabbitMq.Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.VirtualHost) || options.VirtualHost == "/")
        {
            failures.Add("RabbitMq.VirtualHost must identify an isolated environment vhost and cannot be '/'.");
        }

        if (string.IsNullOrWhiteSpace(options.UserName) ||
            string.Equals(options.UserName, "guest", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("RabbitMq.UserName is required and cannot use the shared guest principal.");
        }
        else if (!string.Equals(options.UserName, producerPrincipal, StringComparison.Ordinal))
        {
            failures.Add("RabbitMq.UserName must equal the exact ProducerPrincipal identity.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            failures.Add("RabbitMq.Password is required.");
        }

        if (!options.UseTls && !options.AllowInsecureTransport)
        {
            failures.Add("RabbitMq.UseTls must be enabled unless insecure transport is explicitly allowed.");
        }
        else if (options.UseTls && !string.IsNullOrWhiteSpace(options.TrustedCaCertificatePath))
        {
            ValidateTrustedCaCertificate(options.TrustedCaCertificatePath, failures);
        }

        if (options.UseTls)
        {
            ValidateSafeAbsolutePath(options.ClientCertificatePath, "RabbitMq.ClientCertificatePath", failures);
            ValidateSafeAbsolutePath(options.ClientPrivateKeyPath, "RabbitMq.ClientPrivateKeyPath", failures);
        }

        if (options.AllowInsecureTransport && !IsLocalEnvironment(environment.EnvironmentName))
        {
            failures.Add("RabbitMq.AllowInsecureTransport is permitted only in Development or test environments.");
        }

        if (!options.PublisherConfirmation)
        {
            failures.Add("RabbitMq.PublisherConfirmation must remain enabled.");
        }

        if (!options.DetectUnroutableMessages)
        {
            failures.Add("RabbitMq.DetectUnroutableMessages must remain enabled.");
        }

        if (options.PrefetchCount is < 1 or > 1024)
        {
            failures.Add("RabbitMq.PrefetchCount must be between 1 and 1024.");
        }

        if (options.ConcurrentMessageLimit is < 1 or > 1024)
        {
            failures.Add("RabbitMq.ConcurrentMessageLimit must be between 1 and 1024.");
        }
        else if (options.ConcurrentMessageLimit > options.PrefetchCount)
        {
            failures.Add("RabbitMq.ConcurrentMessageLimit cannot exceed PrefetchCount.");
        }
    }

    private static void ValidateDelivery(MessagingDeliveryOptions options, List<string> failures)
    {
        if (options.ImmediateRetryMilliseconds is null ||
            !options.ImmediateRetryMilliseconds.SequenceEqual(RequiredImmediateRetryMilliseconds))
        {
            failures.Add("Delivery.ImmediateRetryMilliseconds must be exactly [250, 1000].");
        }

        if (options.DelayedRedeliverySeconds is null ||
            !options.DelayedRedeliverySeconds.SequenceEqual(RequiredDelayedRedeliverySeconds))
        {
            failures.Add("Delivery.DelayedRedeliverySeconds must be exactly [30, 120, 600].");
        }

        if (options.StopTimeout < TimeSpan.FromSeconds(5) || options.StopTimeout > TimeSpan.FromMinutes(2))
        {
            failures.Add("Delivery.StopTimeout must be between 5 seconds and 2 minutes.");
        }
    }

    private static void ValidateTrust(MessagingTrustOptions options, string producerPrincipal, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.ActiveKeyId) || options.ActiveKeyId.Length > 120)
            failures.Add("Trust.ActiveKeyId is required and must not exceed 120 characters.");
        ValidateSafeAbsolutePath(options.PrivateKeyPath, "Trust.PrivateKeyPath", failures);
        if (options.MaximumOriginAge <= TimeSpan.Zero || options.MaximumOriginAge > TimeSpan.FromDays(30))
            failures.Add("Trust.MaximumOriginAge must be greater than zero and no more than 30 days.");
        if (options.MaximumFutureClockSkew < TimeSpan.Zero || options.MaximumFutureClockSkew > TimeSpan.FromMinutes(5))
            failures.Add("Trust.MaximumFutureClockSkew must be between zero and five minutes.");
        if (options.MaximumRelayLifetime < TimeSpan.FromMinutes(1) || options.MaximumRelayLifetime > TimeSpan.FromHours(1))
            failures.Add("Trust.MaximumRelayLifetime must be between one minute and one hour.");
        if (options.PublicKeys is null || options.PublicKeys.Count == 0)
        {
            failures.Add("Trust.PublicKeys must contain the active producer verification key.");
            return;
        }

        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in options.PublicKeys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId) || !keyIds.Add(key.KeyId))
                failures.Add("Trust.PublicKeys must use unique non-empty immutable key IDs.");
            ValidatePrincipal(key.Actor, "Trust.PublicKeys.Actor", failures);
            ValidateSafeAbsolutePath(key.PublicKeyPath, "Trust.PublicKeys.PublicKeyPath", failures);
        }
        if (!options.PublicKeys.Any(key => key.KeyId == options.ActiveKeyId && key.Actor == producerPrincipal && !key.Revoked))
            failures.Add("Trust.ActiveKeyId must identify a non-revoked key for the exact ProducerPrincipal.");
    }

    private static void ValidateReplay(MessagingReplayOptions options, List<string> failures)
    {
        if (!string.Equals(options.Actor, "tenantwright-messaging-replay", StringComparison.Ordinal))
            failures.Add("Replay.Actor must be exactly 'tenantwright-messaging-replay'.");
        if (options.MaximumAuthorizationLifetime <= TimeSpan.Zero || options.MaximumAuthorizationLifetime > TimeSpan.FromHours(1))
            failures.Add("Replay.MaximumAuthorizationLifetime must be greater than zero and no more than one hour.");
        if (options.TerminalRetention <= TimeSpan.Zero || options.TerminalRetention > TimeSpan.FromDays(365))
            failures.Add("Replay.TerminalRetention must be greater than zero and no more than 365 days.");
        if (options.MaximumDeathRecords is < 3 or > 32)
            failures.Add("Replay.MaximumDeathRecords must be between 3 and 32 for the fixed delayed-redelivery catalog.");
    }

    private static void ValidateSafeAbsolutePath(string? path, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{propertyName} must be a non-empty absolute path.");
            return;
        }
        try
        {
            MessagingKeyFile.Validate(path, propertyName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or
                                           FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or
                                           IOException or InvalidOperationException)
        {
            failures.Add($"{propertyName} must identify an accessible read-only regular non-reparse file.");
        }
    }

    private static void ValidateTrustedCaCertificate(string path, List<string> failures)
    {
        try
        {
            var validatedPath = MessagingKeyFile.Validate(path, "RabbitMQ trusted CA certificate");
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(validatedPath);
            var basicConstraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            if (basicConstraints is null || !basicConstraints.CertificateAuthority)
                failures.Add("RabbitMq.TrustedCaCertificatePath must contain one CA certificate.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
            PathTooLongException or FileNotFoundException or DirectoryNotFoundException or
            UnauthorizedAccessException or IOException or InvalidOperationException or CryptographicException)
        {
            failures.Add("RabbitMq.TrustedCaCertificatePath must identify an accessible read-only regular CA certificate file.");
        }
    }

    private static void ValidatePrincipal(string? value, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length is < 10 or > 80 ||
            !value.StartsWith("tenantwright-", StringComparison.Ordinal) ||
            value[^1] == '-' ||
            value.Contains("--", StringComparison.Ordinal) ||
            value.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            failures.Add($"{propertyName} must be a lowercase 'tenantwright-<service>' identity.");
        }
    }

    private static bool IsLocalEnvironment(string environmentName)
    {
        return string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
    }
}
