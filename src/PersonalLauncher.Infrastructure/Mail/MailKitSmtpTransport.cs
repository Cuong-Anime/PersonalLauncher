using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using PersonalLauncher.Core.Contracts;
using PersonalLauncher.Core.Models;

namespace PersonalLauncher.Infrastructure.Mail;

public sealed class MailKitSmtpTransport : PersonalLauncher.Core.Contracts.IMailTransport
{
    private readonly int _timeoutMs;

    public MailKitSmtpTransport(int timeoutMs = 20_000)
    {
        _timeoutMs = timeoutMs;
    }

    public async Task<MailSendResult> SendAsync(
        MailSettingsRecord settings,
        string secretCredential,
        string messageId,
        string subject,
        string bodyText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost) ||
            settings.SmtpPort is <= 0 or > 65535 ||
            string.IsNullOrWhiteSpace(settings.SenderAddress) ||
            string.IsNullOrWhiteSpace(settings.RecipientAddress))
        {
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Failed,
                ErrorSummary = "SMTP host, port, sender address, or recipient address is missing."
            };
        }

        if (!MailboxAddress.TryParse(settings.SenderAddress.Trim(), out MailboxAddress? senderMailbox) ||
            !MailboxAddress.TryParse(settings.RecipientAddress.Trim(), out MailboxAddress? recipientMailbox))
        {
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Failed,
                ErrorSummary = "Sender or recipient email address is invalid."
            };
        }

        if (string.IsNullOrEmpty(secretCredential))
        {
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Failed,
                ErrorSummary = "SMTP credential is not configured."
            };
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(settings.SenderDisplayName) ? "PersonalLauncher" : settings.SenderDisplayName,
            senderMailbox.Address));
        message.To.Add(recipientMailbox);
        message.Subject = subject;
        message.MessageId = messageId.Trim('<', '>');
        message.Body = new TextPart("plain") { Text = bodyText };

        using var client = new SmtpClient
        {
            Timeout = _timeoutMs,
            CheckCertificateRevocation = true
            // Intentionally NEVER overriding ServerCertificateValidationCallback to bypass TLS checks!
        };

        bool sendStarted = false;
        try
        {
            SecureSocketOptions tlsOption = settings.TlsMode == MailTlsMode.SslOnConnect
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, tlsOption, ct);

            if (settings.AuthMethod == MailAuthMethod.OAuth2AccessToken)
            {
                var oauth2 = new SaslMechanismOAuth2(settings.SenderAddress, secretCredential);
                await client.AuthenticateAsync(oauth2, ct);
            }
            else
            {
                await client.AuthenticateAsync(settings.SenderAddress, secretCredential, ct);
            }

            sendStarted = true;
            await client.SendAsync(message, ct);

            try
            {
                await client.DisconnectAsync(quit: true, ct);
            }
            catch
            {
                // Server already accepted the message before QUIT
            }

            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Accepted
            };
        }
        catch (AuthenticationException ex)
        {
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Failed,
                ErrorSummary = $"Authentication failed: {Truncate(ex.Message)}"
            };
        }
        catch (SslHandshakeException ex)
        {
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Failed,
                ErrorSummary = $"TLS verification failed: {Truncate(ex.Message)}"
            };
        }
        catch (SmtpCommandException ex)
        {
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Failed,
                ErrorSummary = $"SMTP rejected ({(int)ex.StatusCode}): {Truncate(ex.Message)}"
            };
        }
        catch (Exception ex) when (sendStarted && (ex is IOException or SocketException or TimeoutException or ServiceNotConnectedException or SmtpProtocolException))
        {
            // Timeout / drop after transmission began: server may or may not have queued the message.
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Unknown,
                ErrorSummary = $"Uncertain delivery after transmission: {Truncate(ex.Message)}"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MailSendResult
            {
                Outcome = MailOutboxStatus.Failed,
                ErrorSummary = Truncate(ex.Message)
            };
        }
    }

    private static string Truncate(string? value, int max = 180)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown error";
        string cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }
}
