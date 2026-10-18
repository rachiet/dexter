using System.Text.RegularExpressions;

namespace Dexter;

/// <summary>
/// A request from the phone, parsed from an email:
///   Subject: [dexter] &lt;session-name&gt;
///   Body:    action: new|message
///            folder: recipes        (new only)
///            agent: claude          (new only, optional)
///            ---
///            free text for the agent
/// </summary>
public enum RequestAction
{
    New,      // start a session
    Message,  // send to an existing session
}

public sealed record DexterRequest(
    string SessionName,
    RequestAction Action,
    IReadOnlyDictionary<string, string> Fields,
    string Text);

public static partial class MessageParser
{
    public const string SessionsListName = "sessions";

    [GeneratedRegex(@"^\s*((re|fwd?|aw)\s*:\s*)*\[dexter\]\s*(?<name>.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectRegex();

    // Start of quoted history in a reply: "On <date>, <someone> wrote:" (Gmail may wrap it over two lines).
    [GeneratedRegex(@"^On\s.+?(\r?\n.+?)?\s+wrote:\s*$", RegexOptions.Multiline)]
    private static partial Regex QuoteHeaderRegex();

    [GeneratedRegex(@"^(?<key>[A-Za-z_]+)\s*:\s*(?<value>.*)$")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugRegex();

    /// <summary>Returns the session name from a "[dexter] name" subject, or null if it isn't a Dexter subject.</summary>
    public static string? SessionNameFromSubject(string? subject)
    {
        if (subject is null) return null;
        var m = SubjectRegex().Match(subject);
        return m.Success ? NormalizeName(m.Groups["name"].Value) : null;
    }

    /// <summary>"Recipe App!" -> "recipe-app". Empty if nothing usable remains.</summary>
    public static string NormalizeName(string raw)
    {
        var slug = NonSlugRegex().Replace(raw.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    public static bool TryParse(string? subject, string body, out DexterRequest? request, out string error)
    {
        request = null;
        error = "";

        var name = SessionNameFromSubject(subject);
        if (string.IsNullOrEmpty(name))
        {
            error = "The subject must be \"[dexter] <session-name>\".";
            return false;
        }

        var lines = StripQuotedReply(body).Replace("\r\n", "\n").Split('\n');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;

        for (; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "---") { i++; break; }
            if (line.Length == 0) continue;
            var m = FieldRegex().Match(line);
            if (!m.Success) break; // free text started without a "---" separator; be tolerant
            fields[m.Groups["key"].Value.ToLowerInvariant()] = m.Groups["value"].Value.Trim();
        }

        var text = string.Join('\n', lines[i..]).Trim();

        if (!fields.TryGetValue("action", out var action) || action.Length == 0)
        {
            error = "The email body must start with an \"action:\" line (new or message).";
            return false;
        }

        RequestAction? parsed = action.ToLowerInvariant() switch
        {
            "new" => RequestAction.New,
            "message" => RequestAction.Message,
            _ => null,
        };
        if (parsed is null)
        {
            error = $"Unknown action \"{action}\". Use \"new\" or \"message\".";
            return false;
        }

        request = new DexterRequest(name, parsed.Value, fields, text);
        return true;
    }

    /// <summary>Drops the quoted conversation that mail clients append to replies.</summary>
    public static string StripQuotedReply(string body)
    {
        var text = body.Replace("\r\n", "\n");
        var cut = text.Length;

        var quote = QuoteHeaderRegex().Match(text);
        if (quote.Success) cut = Math.Min(cut, quote.Index);

        foreach (var marker in new[] { "\n-----Original Message-----", "\n________________________________" })
        {
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0) cut = Math.Min(cut, idx);
        }

        // A block of "> " lines also marks quoted history.
        var lines = text[..cut].Split('\n');
        var kept = lines.TakeWhile(l => !l.TrimStart().StartsWith('>'));
        return string.Join('\n', kept).TrimEnd();
    }
}
