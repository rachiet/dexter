using System.Diagnostics;
using System.Text.Json;

namespace Dexter;

public sealed record AgentRun(
    string SessionName,
    string WorkingDirectory,
    string AgentSessionId,
    bool IsNewSession,
    string Prompt);

public sealed record AgentResult(
    string Text,
    string? AgentSessionId,
    decimal? CostUsd,
    IReadOnlyList<string> Denials,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration)
{
    public bool Failed => TimedOut || ExitCode != 0;
}

/// <summary>Runs one bounded step of a coding agent and returns what it said.</summary>
public interface IAgentAdapter
{
    string Name { get; }

    /// <summary>Session id to use for a brand-new session.</summary>
    string NewSessionId();

    Task<AgentResult> RunAsync(AgentRun run, CancellationToken ct);
}

/// <summary>
/// Claude Code in headless mode. Auto mode lets routine work through; with nobody at the
/// keyboard, anything that would prompt is refused and reported back instead of hanging.
/// </summary>
public sealed class ClaudeAgent(Settings settings, ILogger<ClaudeAgent> log) : IAgentAdapter
{
    public string Name => "claude";

    public string NewSessionId() => Guid.NewGuid().ToString();

    public async Task<AgentResult> RunAsync(AgentRun run, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(settings.ClaudePath)
        {
            WorkingDirectory = run.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "-p", run.Prompt,
            run.IsNewSession ? "--session-id" : "--resume", run.AgentSessionId,
            "--name", $"dexter-{run.SessionName}",
            "--permission-mode", "auto",
            "--permission-prompts", "none",
            "--max-budget-usd", settings.MaxBudgetUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--output-format", "json",
        })
        {
            psi.ArgumentList.Add(arg);
        }
        if (File.Exists(settings.AgentRulesFile))
        {
            psi.ArgumentList.Add("--append-system-prompt-file");
            psi.ArgumentList.Add(settings.AgentRulesFile);
        }

        log.LogInformation("Running claude for {Session} in {Dir} ({Mode})",
            run.SessionName, run.WorkingDirectory, run.IsNewSession ? "new" : "resume");

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start claude");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.RunTimeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }

        var output = await stdout;
        var errors = await stderr;
        watch.Stop();

        if (timedOut)
        {
            return new AgentResult(
                $"Stopped after {settings.RunTimeout.TotalMinutes:0} minutes (Dexter's time limit for one step).",
                null, null, [], -1, true, watch.Elapsed);
        }

        return Parse(output, errors, process.ExitCode, watch.Elapsed);
    }

    private static AgentResult Parse(string stdout, string stderr, int exitCode, TimeSpan duration)
    {
        var json = stdout.Trim();
        var start = json.LastIndexOf("\n{", StringComparison.Ordinal);
        if (start >= 0) json = json[(start + 1)..];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
            var sessionId = root.TryGetProperty("session_id", out var s) ? s.GetString() : null;
            decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;

            var denials = new List<string>();
            if (root.TryGetProperty("permission_denials", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in d.EnumerateArray())
                {
                    var tool = item.TryGetProperty("tool_name", out var t) ? t.GetString() : null;
                    var input = item.TryGetProperty("tool_input", out var i) ? Summarize(i) : null;
                    denials.Add(input is null ? tool ?? "unknown" : $"{tool}: {input}");
                }
            }

            if (string.IsNullOrWhiteSpace(text) && isError)
                text = "The agent reported an error." + (stderr.Length > 0 ? "\n\n" + Tail(stderr) : "");
            return new AgentResult(text, sessionId, cost, denials, isError && exitCode == 0 ? 1 : exitCode, false, duration);
        }
        catch (JsonException)
        {
            var text = stdout.Trim().Length > 0 ? stdout.Trim() : "The agent produced no output.";
            if (stderr.Trim().Length > 0) text += "\n\n" + Tail(stderr);
            return new AgentResult(text, null, null, [], exitCode == 0 ? 1 : exitCode, false, duration);
        }
    }

    private static string? Summarize(JsonElement input)
    {
        foreach (var key in new[] { "command", "file_path", "url", "pattern" })
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return Truncate(v.GetString()!, 120);
        return null;
    }

    private static string Tail(string text) => text.Length <= 1500 ? text.Trim() : "…" + text[^1500..].Trim();

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
