using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Dexter;

/// <summary>
/// Turns a request email into work: validates it, records the session, queues an agent run,
/// and replies in the request's thread. Runs for the same project folder happen one at a time.
/// </summary>
public sealed partial class Dispatcher(
    Settings settings,
    SessionStore store,
    IMailbox mailbox,
    StatusReporter reporter,
    IEnumerable<IAgentAdapter> agents,
    ILogger<Dispatcher> log)
{
    private sealed record Job(string SessionName, string Prompt, IncomingMail Mail);

    private readonly Dictionary<string, Channel<Job>> _queues = new();
    private readonly object _queuesLock = new();
    private CancellationToken _stopping;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex FolderNameRegex();

    public void Start(CancellationToken stopping) => _stopping = stopping;

    public async Task HandleAsync(IncomingMail mail, CancellationToken ct)
    {
        log.LogInformation("Request \"{Subject}\" (thread {Thread})", mail.Subject, mail.ThreadId);

        if (MessageParser.SessionNameFromSubject(mail.Subject) == MessageParser.SessionsListName)
            return; // the status list thread is Dexter's own; nothing to do

        if (!MessageParser.TryParse(mail.Subject, mail.Body, out var request, out var error))
        {
            await ReplyAsync(mail, $"I couldn't read that request: {error}\n\n{FormatHelp}", ct);
            return;
        }

        switch (request!.Action)
        {
            case "new":
                await StartSessionAsync(request, mail, ct);
                break;
            case "message":
                await MessageSessionAsync(request, mail, ct);
                break;
            default:
                await ReplyAsync(mail, $"Unknown action \"{request.Action}\". Use \"new\" or \"message\".\n\n{FormatHelp}", ct);
                break;
        }
    }

    private async Task StartSessionAsync(DexterRequest request, IncomingMail mail, CancellationToken ct)
    {
        var name = request.SessionName;
        if (name.Length == 0 || name == MessageParser.SessionsListName)
        {
            await ReplyAsync(mail, $"\"{name}\" can't be used as a session name.", ct);
            return;
        }
        if (store.GetByName(name) is { } existing)
        {
            await ReplyAsync(mail, $"A session named \"{name}\" already exists (in {Settings.Display(existing.Folder)}). " +
                "Pick another name, or reply in its thread to send it a message.", ct);
            return;
        }

        var agentName = request.Fields.GetValueOrDefault("agent") is { Length: > 0 } a ? a.ToLowerInvariant() : settings.DefaultAgent;
        var agent = agents.FirstOrDefault(x => x.Name == agentName);
        if (agent is null)
        {
            await ReplyAsync(mail, $"I don't know the agent \"{agentName}\". Available: {string.Join(", ", agents.Select(x => x.Name))}.", ct);
            return;
        }

        var folderName = request.Fields.GetValueOrDefault("folder") is { Length: > 0 } f ? f : name;
        if (ResolveFolder(folderName) is not { } folder)
        {
            await ReplyAsync(mail, $"I can't use \"{folderName}\" as a folder. It must be a plain folder name inside " +
                $"{Settings.Display(settings.Workspace)} (letters, numbers, dots, dashes, underscores).", ct);
            return;
        }

        if (request.Text.Length == 0)
        {
            await ReplyAsync(mail, "What should the new session do? Put the task after the \"---\" line.", ct);
            return;
        }

        var created = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        if (!Directory.Exists(Path.Combine(folder, ".git"))) await GitInitAsync(folder);

        var now = DateTimeOffset.Now;
        store.Insert(new Session(name, mail.ThreadId, folder, agent.Name, agent.NewSessionId(),
            SessionStatus.Queued, null, null, now, now));

        await ReplyAsync(mail,
            $"Started session \"{name}\" in {Settings.Display(folder)}{(created ? " (new folder)" : "")} using {agent.Name}. " +
            "Working on it now. I'll reply in this thread when the first step is done.", ct);
        Enqueue(new Job(name, request.Text, mail));
    }

    private async Task MessageSessionAsync(DexterRequest request, IncomingMail mail, CancellationToken ct)
    {
        var session = store.GetByThread(mail.ThreadId) ?? store.GetByName(request.SessionName);
        if (session is null)
        {
            var names = store.All().Select(s => s.Name).ToList();
            await ReplyAsync(mail, $"There's no session called \"{request.SessionName}\". " +
                (names.Count == 0 ? "No sessions exist yet." : $"Current sessions: {string.Join(", ", names)}."), ct);
            return;
        }
        if (request.Text.Length == 0)
        {
            await ReplyAsync(mail, "The message was empty. Put your instruction after the \"---\" line.", ct);
            return;
        }

        if (session.Status is SessionStatus.Working or SessionStatus.Queued)
            await ReplyAsync(mail, $"Got it. \"{session.Name}\" is busy with the current step; I'll pass this on right after.", ct);

        Enqueue(new Job(session.Name, request.Text, mail));
    }

    /// <summary>Folder path inside the workspace, or null if the name is unsafe.</summary>
    private string? ResolveFolder(string folderName)
    {
        if (!FolderNameRegex().IsMatch(folderName) || folderName.Contains("..")) return null;
        var path = Path.GetFullPath(Path.Combine(settings.Workspace, folderName));
        if (Path.GetDirectoryName(path) != settings.Workspace) return null;
        if (path == settings.Home) return null; // never point an agent at Dexter itself
        return path;
    }

    private void Enqueue(Job job)
    {
        var folder = store.GetByName(job.SessionName)!.Folder;
        lock (_queuesLock)
        {
            if (!_queues.TryGetValue(folder, out var channel))
            {
                channel = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
                _queues[folder] = channel;
                _ = Task.Run(() => ConsumeAsync(channel.Reader));
            }
            channel.Writer.TryWrite(job);
        }
        store.SetStatus(job.SessionName, SessionStatus.Queued);
    }

    private async Task ConsumeAsync(ChannelReader<Job> reader)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(_stopping))
            {
                try
                {
                    await RunAsync(job);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Run failed for {Session}", job.SessionName);
                    store.RecordOutcome(job.SessionName, SessionStatus.Error, $"Dexter error: {ex.Message}", null, null);
                    await TryAsync(() => ReplyAsync(job.Mail, $"Dexter hit an error running this step: {ex.Message}", CancellationToken.None));
                    await TryAsync(() => reporter.SendAsync(CancellationToken.None));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(Job job)
    {
        var session = store.GetByName(job.SessionName)!;
        var agent = agents.First(a => a.Name == session.Agent);
        var startedKey = $"agent-started:{session.Name}";

        store.SetStatus(session.Name, SessionStatus.Working);
        await reporter.SendAsync(_stopping);

        var runId = store.StartRun(session.Name, job.Prompt);
        var result = await agent.RunAsync(new AgentRun(
            session.Name, session.Folder, session.AgentSessionId,
            IsNewSession: store.Get(startedKey) is null,
            job.Prompt), _stopping);
        store.EndRun(runId, result);
        if (result.AgentSessionId is not null) store.Set(startedKey, "1");

        var outcome = Outcome.From(result);
        store.RecordOutcome(session.Name, outcome.Status, outcome.Summary, outcome.Questions, result.AgentSessionId);

        await ReplyAsync(job.Mail, FormatReply(result), _stopping);
        await reporter.SendAsync(_stopping);
    }

    private static string FormatReply(AgentResult result)
    {
        var sb = new StringBuilder(result.Text.Trim());
        sb.Append("\n\n— ");
        sb.Append(result.Duration.TotalMinutes >= 1 ? $"{(int)result.Duration.TotalMinutes}m {result.Duration.Seconds}s" : $"{result.Duration.Seconds}s");
        if (result.Failed) sb.Append(result.TimedOut ? " · timed out" : $" · exit code {result.ExitCode}");
        if (result.Denials.Count > 0)
        {
            sb.Append("\n\nAuto mode refused these (reply to allow or change course):");
            foreach (var d in result.Denials.Distinct()) sb.Append($"\n- {d}");
        }
        return sb.ToString();
    }

    private Task<string> ReplyAsync(IncomingMail mail, string body, CancellationToken ct)
    {
        var subject = mail.Subject.TrimStart().StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? mail.Subject : $"Re: {mail.Subject}";
        return mailbox.SendAsync(new OutgoingMail(subject, body, mail.MessageId, [mail.MessageId]), ct);
    }

    private async Task GitInitAsync(string folder)
    {
        try
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["init", "-q"])
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (git is not null) await git.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning("git init failed in {Folder}: {Message}", folder, ex.Message);
        }
    }

    private async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { log.LogWarning("Follow-up failed: {Message}", ex.Message); }
    }

    private const string FormatHelp = """
        Format:
        Subject: [dexter] <session-name>

        action: new            (or: message)
        folder: <folder-name>  (new only)
        ---
        <what you want done>
        """;
}

/// <summary>Reads the STATUS / QUESTIONS lines the agent rules ask every reply to end with.</summary>
public static partial class Outcome
{
    [GeneratedRegex(@"^\W*STATUS\W*:\s*(?<v>.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^\W*QUESTIONS\W*:\s*(?<v>[\s\S]*)\z", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex QuestionsBlock();

    public static (string Status, string? Summary, string? Questions) From(AgentResult result)
    {
        var text = result.Text.Trim();
        if (result.Failed)
            return (SessionStatus.Error, FirstLine(text), null);

        var status = StatusLine().Matches(text).LastOrDefault()?.Groups["v"].Value.Trim();
        var questions = QuestionsBlock().Match(text) is { Success: true } q ? q.Groups["v"].Value.Trim() : null;
        if (questions is not null && Regex.IsMatch(questions, @"^(none|no|n/?a|-)\.?$", RegexOptions.IgnoreCase))
            questions = null;

        return (questions is null ? SessionStatus.Idle : SessionStatus.Waiting,
            status ?? FirstLine(text),
            questions is null ? null : Truncate(questions, 500));
    }

    private static string FirstLine(string text) =>
        Truncate(text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "", 200);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
