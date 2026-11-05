using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Emailing;

internal interface ISmtpTransportFactory
{
    ISmtpTransportClient Create();
}

internal interface ISmtpTransportClient : IDisposable
{
    Task ConnectAsync(
        string host,
        int port,
        SecureSocketOptions options,
        CancellationToken cancellationToken = default);

    Task AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default);

    Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken = default);

    Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default);
}

internal sealed class SmtpTransportFactory : ISmtpTransportFactory, ITransientDependency
{
    public ISmtpTransportClient Create()
    {
        return new MailKitSmtpTransportClient(new SmtpClient());
    }
}

internal sealed class SmtpMessageDispatcher : ITransientDependency
{
    private readonly ISmtpTransportFactory _transportFactory;
    private readonly ILogger<SmtpMessageDispatcher> _logger;

    public SmtpMessageDispatcher(
        ISmtpTransportFactory transportFactory,
        ILogger<SmtpMessageDispatcher> logger)
    {
        _transportFactory = transportFactory;
        _logger = logger;
    }

    public async Task<string> SendAsync(
        string host,
        int port,
        SecureSocketOptions secureSocketOptions,
        string? userName,
        string? password,
        MimeMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(userName) && string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("SMTP password is required when a user name is configured.");
        }

        using var client = _transportFactory.Create();
        await client.ConnectAsync(host, port, secureSocketOptions, cancellationToken);

        if (!string.IsNullOrWhiteSpace(userName))
        {
            await client.AuthenticateAsync(userName, password!, cancellationToken);
        }

        var response = await client.SendAsync(message, cancellationToken);
        try
        {
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "SMTP disconnect failed after the provider accepted the message.");
        }

        return response;
    }
}

internal sealed class MailKitSmtpTransportClient(SmtpClient client) : ISmtpTransportClient
{
    public Task ConnectAsync(
        string host,
        int port,
        SecureSocketOptions options,
        CancellationToken cancellationToken = default)
    {
        return client.ConnectAsync(host, port, options, cancellationToken);
    }

    public Task AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        return client.AuthenticateAsync(userName, password, cancellationToken);
    }

    public Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        return client.SendAsync(message, cancellationToken);
    }

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
    {
        return client.DisconnectAsync(quit, cancellationToken);
    }

    public void Dispose()
    {
        client.Dispose();
    }
}
