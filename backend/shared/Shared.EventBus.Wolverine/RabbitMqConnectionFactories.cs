using System.Security.Cryptography.X509Certificates;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// Builds the RabbitMQ connection factory for both provider connections (Wolverine's listener connection and the
/// confirmed publisher connection) from the validated <c>Messaging:RabbitMq</c> settings.
/// </summary>
/// <remarks>
/// TLS presents the configured client certificate and, when <see cref="MessagingRabbitMqOptions.TrustedCaCertificatePath"/>
/// is set, pins the server chain to that CA through <see cref="RabbitMqServerCertificateTrust"/>. Plain AMQP is used only
/// when <see cref="MessagingRabbitMqOptions.UseTls"/> is off, which <see cref="MessagingOptionsValidator"/> permits only
/// with <see cref="MessagingRabbitMqOptions.AllowInsecureTransport"/> in a local environment. The connection name is the
/// host's <see cref="MessagingOptions.ServiceName"/>.
/// </remarks>
internal static class RabbitMqConnectionFactories
{
    public static ConnectionFactory Create(MessagingOptions options, bool automaticRecovery)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rabbit = options.RabbitMq;
        var factory = new ConnectionFactory
        {
            HostName = rabbit.HostName,
            Port = rabbit.Port,
            VirtualHost = rabbit.VirtualHost,
            UserName = rabbit.UserName,
            Password = rabbit.Password,
            ClientProvidedName = options.ServiceName,
            AutomaticRecoveryEnabled = automaticRecovery,
            TopologyRecoveryEnabled = automaticRecovery
        };

        if (rabbit.UseTls)
        {
            factory.Ssl = new SslOption
            {
                Enabled = true,
                ServerName = rabbit.HostName,
                Certs = new X509CertificateCollection
                {
                    X509Certificate2.CreateFromPemFile(rabbit.ClientCertificatePath, rabbit.ClientPrivateKeyPath)
                },
                CertificateValidationCallback = string.IsNullOrWhiteSpace(rabbit.TrustedCaCertificatePath)
                    ? null
                    : RabbitMqServerCertificateTrust.CreateValidationCallback(rabbit.TrustedCaCertificatePath)
            };
        }

        return factory;
    }
}
