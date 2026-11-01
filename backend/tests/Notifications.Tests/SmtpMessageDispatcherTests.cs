using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Notifications.Application.Emailing;
using Xunit;

namespace Notifications.Tests;

public class SmtpMessageDispatcherTests
{
    [Fact]
    public async Task SendAsync_fails_before_opening_a_connection_when_a_configured_user_has_no_password()
    {
        var transportFactory = new RecordingTransportFactory();
        var dispatcher = new SmtpMessageDispatcher(
            transportFactory,
            NullLogger<SmtpMessageDispatcher>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.SendAsync(
            "smtp.example.test",
            587,
            SecureSocketOptions.StartTls,
            "notifications@example.test",
            password: null,
            new MimeMessage()));

        Assert.Equal("SMTP password is required when a user name is configured.", exception.Message);
        Assert.Equal(0, transportFactory.CreateCallCount);
    }

    private sealed class RecordingTransportFactory : ISmtpTransportFactory
    {
        public int CreateCallCount { get; private set; }

        public ISmtpTransportClient Create()
        {
            CreateCallCount++;
            return new RecordingTransportClient();
        }
    }

    private sealed class RecordingTransportClient : ISmtpTransportClient
    {
        public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult("accepted");

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
