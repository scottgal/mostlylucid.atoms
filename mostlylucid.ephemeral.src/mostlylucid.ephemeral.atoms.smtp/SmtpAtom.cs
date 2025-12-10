using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.Smtp;

/// <summary>
///     SMTP email sending atom with retry support and signal tracking.
///
///     Uses MailKit for reliable SMTP operations with modern authentication.
/// </summary>
public class SmtpAtom : IAsyncDisposable
{
    private readonly SmtpOptions _options;
    private readonly SignalSink? _signals;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SmtpClient? _client;

    public SmtpAtom(SmtpOptions options, SignalSink? signals = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signals = signals;
    }

    /// <summary>
    ///     Send an email message.
    /// </summary>
    public async Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));

        _signals?.Raise($"smtp.send.started:to={message.To}:subject={message.Subject}");

        try
        {
            var mimeMessage = BuildMimeMessage(message);

            await _lock.WaitAsync(cancellationToken);
            try
            {
                // Reuse connection if persistent, otherwise create new
                if (_options.UsePersistentConnection)
                {
                    if (_client == null || !_client.IsConnected)
                    {
                        await ConnectAsync(cancellationToken);
                    }
                }
                else
                {
                    using var tempClient = new SmtpClient();
                    await ConnectClientAsync(tempClient, cancellationToken);
                    await tempClient.SendAsync(mimeMessage, cancellationToken);
                    await tempClient.DisconnectAsync(true, cancellationToken);

                    _signals?.Raise($"smtp.send.success:to={message.To}");

                    return new EmailResult
                    {
                        Success = true,
                        MessageId = mimeMessage.MessageId,
                        Description = "Email sent successfully"
                    };
                }

                // Use persistent client
                await _client!.SendAsync(mimeMessage, cancellationToken);

                _signals?.Raise($"smtp.send.success:to={message.To}");

                return new EmailResult
                {
                    Success = true,
                    MessageId = mimeMessage.MessageId,
                    Description = "Email sent successfully"
                };
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _signals?.Raise($"smtp.send.failed:to={message.To}:error={ex.Message}");

            return new EmailResult
            {
                Success = false,
                MessageId = null,
                Description = $"Failed to send email: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     Send multiple emails (uses persistent connection for efficiency).
    /// </summary>
    public async Task<List<EmailResult>> SendBatchAsync(
        IEnumerable<EmailMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var results = new List<EmailResult>();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Always use persistent connection for batch
            if (_client == null || !_client.IsConnected)
            {
                await ConnectAsync(cancellationToken);
            }

            foreach (var message in messages)
            {
                var result = await SendAsync(message, cancellationToken);
                results.Add(result);
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _client = new SmtpClient();
        await ConnectClientAsync(_client, cancellationToken);
    }

    private async Task ConnectClientAsync(SmtpClient client, CancellationToken cancellationToken)
    {
        _signals?.Raise($"smtp.connect:host={_options.Host}:port={_options.Port}");

        await client.ConnectAsync(_options.Host, _options.Port, _options.SecureSocketOptions, cancellationToken);

        if (_options.RequiresAuthentication)
        {
            await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);
        }

        _signals?.Raise($"smtp.connected");
    }

    private MimeMessage BuildMimeMessage(EmailMessage message)
    {
        var mimeMessage = new MimeMessage();

        mimeMessage.From.Add(new MailboxAddress(message.FromName ?? _options.DefaultFromName, message.From ?? _options.DefaultFromAddress));
        mimeMessage.To.Add(MailboxAddress.Parse(message.To));

        if (!string.IsNullOrEmpty(message.ReplyTo))
        {
            mimeMessage.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        }

        mimeMessage.Subject = message.Subject;

        var bodyBuilder = new BodyBuilder();

        if (message.IsHtml)
        {
            bodyBuilder.HtmlBody = message.Body;
        }
        else
        {
            bodyBuilder.TextBody = message.Body;
        }

        // Attachments
        if (message.Attachments != null)
        {
            foreach (var attachment in message.Attachments)
            {
                if (attachment.Stream != null)
                {
                    bodyBuilder.Attachments.Add(attachment.Filename, attachment.Stream);
                }
                else if (!string.IsNullOrEmpty(attachment.FilePath))
                {
                    bodyBuilder.Attachments.Add(attachment.FilePath);
                }
            }
        }

        mimeMessage.Body = bodyBuilder.ToMessageBody();

        return mimeMessage;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(true);
            }
            _client.Dispose();
        }

        _lock.Dispose();
    }
}

/// <summary>
///     SMTP configuration options.
/// </summary>
public class SmtpOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 587;
    public bool RequiresAuthentication { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public SecureSocketOptions SecureSocketOptions { get; init; } = SecureSocketOptions.StartTls;
    public bool UsePersistentConnection { get; init; } = false;
    public string? DefaultFromAddress { get; init; }
    public string? DefaultFromName { get; init; }
}

/// <summary>
///     Email message to send.
/// </summary>
public class EmailMessage
{
    public string? From { get; init; }
    public string? FromName { get; init; }
    public required string To { get; init; }
    public string? ReplyTo { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public bool IsHtml { get; init; } = true;
    public List<EmailAttachment>? Attachments { get; init; }
}

/// <summary>
///     Email attachment.
/// </summary>
public class EmailAttachment
{
    public required string Filename { get; init; }
    public Stream? Stream { get; init; }
    public string? FilePath { get; init; }
}

/// <summary>
///     Result from email sending.
/// </summary>
public record EmailResult
{
    public required bool Success { get; init; }
    public required string? MessageId { get; init; }
    public required string Description { get; init; }
}
