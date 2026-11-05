using Notifications.Application.Delivery.Providers;
using MailKit.Net.Smtp;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using Notifications.Application.Contracts.Emailing;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Notifications.Application.Emailing;

internal sealed class MailKitEmailNotificationSender : IEmailNotificationSender, ITransientDependency
{
    private static readonly Meter DeliveryMeter = new("Notifications.Delivery");
    private static readonly Histogram<double> EmailSendDurationMs =
        DeliveryMeter.CreateHistogram<double>("notifications.email.send.duration.ms");

    private readonly IOptions<NotificationsEmailOptions> _options;
    private readonly ICurrentTenant _currentTenant;
    private readonly INotificationProviderConfigResolver _resolver;
    private readonly SmtpMessageDispatcher _smtpDispatcher;

    public MailKitEmailNotificationSender(
        IOptions<NotificationsEmailOptions> options,
        ICurrentTenant currentTenant,
        INotificationProviderConfigResolver resolver,
        SmtpMessageDispatcher smtpDispatcher)
    {
        _options = options;
        _currentTenant = currentTenant;
        _resolver = resolver;
        _smtpDispatcher = smtpDispatcher;
    }

    public async Task<NotificationProviderSendResult> SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        using var sendActivity = NotificationProviderTrace.Start(NotificationChannel.Email);
        var started = Stopwatch.GetTimestamp();
        var tenantId = _currentTenant.Id;

        try
        {
            var resolved = await _resolver.ResolveAsync(tenantId, NotificationChannel.Email, cancellationToken);
            var config = resolved != null ? EmailProviderConfigAppService.DeserializeSettings(resolved.SettingsJson) : null;

            var smtp = _options.Value.Smtp;
            var host = config?.Host?.Trim() is { Length: > 0 } h ? h : smtp.Host;
            var port = (config?.Port ?? 0) > 0 ? config!.Port : smtp.Port;
            var userName = config?.UserName?.Trim() ?? smtp.UserName;
            var password = resolved?.Secret is { Length: > 0 } secret ? secret : smtp.Password;
            var fromAddress = config?.FromAddress?.Trim() is { Length: > 0 } fa ? fa : smtp.FromAddress;
            var fromName = config?.FromName?.Trim() ?? smtp.FromName;
            var secureSocketOption = config?.SecureSocketOption?.Trim() is { Length: > 0 } sso ? sso : smtp.SecureSocketOption;

            if (string.IsNullOrWhiteSpace(host))
            {
                return NotificationProviderSendResult.Unavailable("smtp_host_missing");
            }
            if (string.IsNullOrWhiteSpace(fromAddress))
            {
                return NotificationProviderSendResult.Unavailable("smtp_sender_missing");
            }
            if (string.IsNullOrWhiteSpace(to))
            {
                return NotificationProviderSendResult.PermanentRejection("email_recipient_missing");
            }

            var message = new MimeMessage();
            message.MessageId = $"{Guid.CreateVersion7():N}@tenantwright.invalid";
            message.From.Add(string.IsNullOrWhiteSpace(fromName)
                ? MailboxAddress.Parse(fromAddress)
                : new MailboxAddress(fromName, fromAddress));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject ?? string.Empty;
            message.Body = new TextPart(TextFormat.Html) { Text = htmlBody ?? string.Empty };

            var secureSocketOptions = EmailProviderConfigSmtpHelpers.ParseSecureSocketOptions(secureSocketOption);
            await _smtpDispatcher.SendAsync(
                host,
                port,
                secureSocketOptions,
                userName,
                password,
                message,
                cancellationToken);
            var result = NotificationProviderSendResult.Success(message.MessageId);
            return resolved is null ? result : result.WithConfiguration(resolved.ConfigId);
        }
        catch (SmtpCommandException rejection) when (rejection.ErrorCode is SmtpErrorCode.MessageNotAccepted or SmtpErrorCode.RecipientNotAccepted)
        {
            var status = (int)rejection.StatusCode;
            var code = $"smtp_{status}";
            return status is >= 400 and < 500 ? NotificationProviderSendResult.RetryableRejection(code) :
                status is >= 500 and < 600 ? NotificationProviderSendResult.PermanentRejection(code) : NotificationProviderSendResult.Ambiguous(code);
        }
        catch (SmtpCommandException)
        {
            return NotificationProviderSendResult.Unavailable("smtp_configuration_rejected");
        }
        finally
        {
            EmailSendDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
