using System.Text;

namespace Dexter;

/// <summary>
/// Keeps the "[dexter] sessions" thread current. The phone assistant answers "what am I running?"
/// by reading the newest message here, without waking any agent.
/// </summary>
public sealed class StatusReporter(SessionStore store, IMailbox mailbox, ILogger<StatusReporter> log)
{
    private const string RootKey = "sessions-thread-root";
    private const string LastKey = "sessions-thread-last";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SendAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var root = store.Get(RootKey);
            var last = store.Get(LastKey);
            var id = await mailbox.SendAsync(new OutgoingMail(
                $"[dexter] {MessageParser.SessionsListName}",
                Render(store.All()),
                InReplyTo: last ?? root,
                References: root is null ? null : [root]), ct);
            if (root is null) store.Set(RootKey, id);
            store.Set(LastKey, id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Could not send sessions list: {Message}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string Render(IReadOnlyList<Session> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Dexter sessions · updated {DateTimeOffset.Now:ddd d MMM HH:mm} on {Environment.MachineName}");
        sb.AppendLine();

        if (sessions.Count == 0)
        {
            sb.AppendLine("No sessions yet. Ask Dexter to start one.");
            return sb.ToString();
        }

        foreach (var s in sessions)
        {
            sb.AppendLine($"{s.Name} — {Describe(s.Status)}");
            sb.AppendLine($"  folder: {Settings.Display(s.Folder)} · agent: {s.Agent} · last activity {s.UpdatedAt:ddd HH:mm}");
            if (!string.IsNullOrWhiteSpace(s.Summary)) sb.AppendLine($"  last: {s.Summary}");
            if (!string.IsNullOrWhiteSpace(s.Questions)) sb.AppendLine($"  waiting on you: {s.Questions}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Describe(string status) => status switch
    {
        SessionStatus.Waiting => "waiting on you",
        SessionStatus.Error => "stopped with an error",
        _ => status,
    };
}
