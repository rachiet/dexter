using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Dexter;

public sealed record IncomingMail(
    string MessageId,
    string ThreadId,
    string Subject,
    string Body,
    DateTimeOffset Date);

public sealed record OutgoingMail(
    string Subject,
    string Body,
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null);

/// <summary>Anything Dexter can read requests from and write replies to.</summary>
public interface IMailbox
{
    /// <summary>Requests not yet handled, oldest first.</summary>
    Task<IReadOnlyList<IncomingMail>> FetchNewAsync(Func<string, bool> isProcessed, CancellationToken ct);

    /// <summary>Returns when new mail may have arrived, or after <paramref name="max"/> at the latest.</summary>
    Task WaitForChangeAsync(TimeSpan max, CancellationToken ct);

    /// <summary>Sends a mail and returns its Message-ID.</summary>
    Task<string> SendAsync(OutgoingMail mail, CancellationToken ct);
}

/// <summary>
/// Gmail mailbox for a dedicated Dexter account. The phone assistant sends from this account to
/// itself, so requests are messages with Gmail's \Sent label. Dexter's own mail carries an
/// X-Dexter header and is skipped, which also means mail from anyone else is ignored.
/// </summary>
public sealed class GmailMailbox(Settings settings, ILogger<GmailMailbox> log) : IMailbox, IDisposable
{
    public const string DexterHeader = "X-Dexter";
    // Certificates are still fully validated; only the online revocation lookup (often
    // unreachable on macOS) is skipped.
    private readonly ImapClient _imap = new() { CheckCertificateRevocation = false };
    private IMailFolder? _inbox;

    public async Task<IReadOnlyList<IncomingMail>> FetchNewAsync(Func<string, bool> isProcessed, CancellationToken ct)
    {
        var folder = await EnsureConnectedAsync(ct);
        var uids = await folder.SearchAsync(SearchQuery.GMailRawSearch("in:sent subject:dexter newer_than:7d"), ct);
        if (uids.Count == 0) return [];

        var request = new FetchRequest(
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
            MessageSummaryItems.GMailThreadId | MessageSummaryItems.GMailLabels)
        {
            Headers = new HeaderSet([DexterHeader]),
        };

        var result = new List<IncomingMail>();
        foreach (var s in await folder.FetchAsync(uids, request, ct))
        {
            var messageId = s.Envelope?.MessageId ?? $"uid-{s.UniqueId.Id}";
            if (isProcessed(messageId)) continue;
            if (s.Headers?.Contains(DexterHeader) == true) continue;
            if (s.GMailLabels?.Contains("\\Sent") != true) continue;
            if (MessageParser.SessionNameFromSubject(s.Envelope?.Subject) is null) continue;

            var message = await folder.GetMessageAsync(s.UniqueId, ct);
            result.Add(new IncomingMail(
                messageId,
                s.GMailThreadId?.ToString() ?? messageId,
                s.Envelope?.Subject ?? "",
                message.TextBody ?? HtmlToText(message.HtmlBody),
                s.Envelope?.Date ?? DateTimeOffset.Now));
        }
        return result.OrderBy(m => m.Date).ToList();
    }

    public async Task WaitForChangeAsync(TimeSpan max, CancellationToken ct)
    {
        var folder = await EnsureConnectedAsync(ct);
        if (!_imap.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }

        using var done = new CancellationTokenSource(max);
        void OnChange(object? sender, EventArgs e) => done.Cancel();
        folder.CountChanged += OnChange;
        try
        {
            await _imap.IdleAsync(done.Token, ct);
        }
        finally
        {
            folder.CountChanged -= OnChange;
        }
    }

    public async Task<string> SendAsync(OutgoingMail mail, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Dexter", settings.GmailAddress));
        message.To.Add(MailboxAddress.Parse(settings.GmailAddress));
        message.Subject = mail.Subject;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        message.Headers.Add(DexterHeader, "agent");
        if (mail.InReplyTo is not null) message.InReplyTo = mail.InReplyTo;
        foreach (var reference in mail.References ?? []) message.References.Add(reference);
        message.Body = new TextPart("plain") { Text = mail.Body };

        using var smtp = new SmtpClient { CheckCertificateRevocation = false };
        await smtp.ConnectAsync("smtp.gmail.com", 465, SecureSocketOptions.SslOnConnect, ct);
        await smtp.AuthenticateAsync(settings.GmailAddress, settings.GmailAppPassword, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
        log.LogInformation("Sent \"{Subject}\"", mail.Subject);
        return message.MessageId;
    }

    private async Task<IMailFolder> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_imap.IsConnected && _imap.IsAuthenticated && _inbox is { IsOpen: true }) return _inbox;

        if (_imap.IsConnected) await _imap.DisconnectAsync(true, ct);
        await _imap.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect, ct);
        await _imap.AuthenticateAsync(settings.GmailAddress, settings.GmailAppPassword, ct);
        // Watch the Inbox: Gmail sends IDLE notifications for it promptly (not for All Mail), and
        // mail the account sends to itself lands there too.
        _inbox = _imap.Inbox;
        await _inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        log.LogInformation("Connected to Gmail as {Address}", settings.GmailAddress);
        return _inbox;
    }

    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<(br|/p|/div)\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text);
    }

    public void Dispose() => _imap.Dispose();
}
