using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Logrr.Notify;

/// <summary>Sends one rendered email for an SMTP destination. Throws on any delivery failure.</summary>
public interface ISmtpSender
{
    Task SendAsync(Destination destination, string subject, string body, string? password, CancellationToken ct);
}

/// <summary>MailKit-backed SMTP sender. Supports plain, STARTTLS (587), and implicit SSL (465).</summary>
public sealed class MailKitSmtpSender : ISmtpSender
{
    public async Task SendAsync(Destination destination, string subject, string body, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destination.SmtpHost))
        {
            throw new InvalidOperationException("SMTP host is not configured.");
        }
        if (string.IsNullOrWhiteSpace(destination.SmtpFrom))
        {
            throw new InvalidOperationException("SMTP from-address is not configured.");
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(destination.SmtpFrom));
        var recipients = SplitRecipients(destination.SmtpTo);
        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("SMTP destination has no recipients.");
        }
        foreach (var to in recipients)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }
        message.Subject = subject;

        var builder = new BodyBuilder();
        if (destination.IsHtmlEmail) { builder.HtmlBody = body; } else { builder.TextBody = body; }
        message.Body = builder.ToMessageBody();

        var security = destination.SmtpSecurity switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(destination.SmtpHost, destination.SmtpPort, security, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(destination.SmtpUsername))
        {
            await client.AuthenticateAsync(destination.SmtpUsername, password ?? "", ct).ConfigureAwait(false);
        }
        await client.SendAsync(message, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }

    /// <summary>Split a comma/semicolon/whitespace-separated recipient list, dropping blanks.</summary>
    public static List<string> SplitRecipients(string? to) =>
        (to ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
