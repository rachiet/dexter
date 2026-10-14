namespace Dexter;

/// <summary>
/// Dexter configuration. Read from environment variables, with a .env file in
/// DEXTER_HOME (default: current directory) filling in anything not already set.
/// </summary>
public sealed record Settings(
    string Home,
    string GmailAddress,
    string GmailAppPassword,
    string Workspace,
    string DataDir,
    string DefaultAgent,
    string ClaudePath,
    string AgentRulesFile,
    decimal MaxBudgetUsd,
    TimeSpan RunTimeout)
{
    public string DatabasePath => Path.Combine(DataDir, "dexter.db");

    public static Settings Load()
    {
        var home = Path.GetFullPath(ExpandHome(Environment.GetEnvironmentVariable("DEXTER_HOME") ?? Directory.GetCurrentDirectory()));
        EnvFile.Load(Path.Combine(home, ".env"));

        string Get(string key, string? fallback = null)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            return fallback ?? throw new InvalidOperationException($"Missing required setting {key} (set it in {Path.Combine(home, ".env")})");
        }

        return new Settings(
            Home: home,
            GmailAddress: Get("GMAIL_ADDRESS"),
            GmailAppPassword: Get("GMAIL_APP_PASSWORD").Replace(" ", ""),
            Workspace: Path.GetFullPath(ExpandHome(Get("DEXTER_WORKSPACE", "~/Projects"))),
            DataDir: Path.GetFullPath(ExpandHome(Get("DEXTER_DATA_DIR", Path.Combine(home, "data")))),
            DefaultAgent: Get("DEXTER_DEFAULT_AGENT", "claude"),
            ClaudePath: Get("DEXTER_CLAUDE_PATH", "claude"),
            AgentRulesFile: Path.GetFullPath(ExpandHome(Get("DEXTER_AGENT_RULES", Path.Combine(home, "agent-rules.md")))),
            MaxBudgetUsd: decimal.Parse(Get("DEXTER_MAX_BUDGET_USD", "5"), System.Globalization.CultureInfo.InvariantCulture),
            RunTimeout: TimeSpan.FromMinutes(double.Parse(Get("DEXTER_RUN_TIMEOUT_MINUTES", "30"), System.Globalization.CultureInfo.InvariantCulture)));
    }

    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;

    /// <summary>Shortens a path under the home directory to ~/... for display.</summary>
    public static string Display(string path)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(userHome + "/") ? "~" + path[userHome.Length..] : path;
    }
}

/// <summary>Minimal .env reader: KEY=value lines, # comments, optional quotes. Never overrides real env vars.</summary>
public static class EnvFile
{
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
