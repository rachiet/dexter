using Microsoft.Data.Sqlite;

namespace Dexter;

public enum SessionStatus
{
    Queued,
    Working,
    Idle,
    Waiting,   // agent asked the user a question
    Error,
}

public sealed record Session(
    string Name,
    string ThreadId,
    string Folder,
    string Agent,
    string AgentSessionId,
    SessionStatus Status,
    string? Summary,
    string? Questions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Dexter's own records in SQLite: sessions, handled email ids, agent runs, and small key/value state.
/// All access goes through one connection behind a lock; every operation is tiny.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public SessionStore(Settings settings)
    {
        Directory.CreateDirectory(settings.DataDir);
        _db = new SqliteConnection($"Data Source={settings.DatabasePath}");
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS sessions (
                name             TEXT PRIMARY KEY,
                thread_id        TEXT NOT NULL,
                folder           TEXT NOT NULL,
                agent            TEXT NOT NULL,
                agent_session_id TEXT NOT NULL,
                status           TEXT NOT NULL,
                summary          TEXT,
                questions        TEXT,
                created_at       TEXT NOT NULL,
                updated_at       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_thread ON sessions(thread_id);
            CREATE TABLE IF NOT EXISTS processed_messages (
                message_id   TEXT PRIMARY KEY,
                processed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session     TEXT NOT NULL,
                started_at  TEXT NOT NULL,
                ended_at    TEXT,
                exit_code   INTEGER,
                timed_out   INTEGER,
                prompt      TEXT,
                result      TEXT
            );
            CREATE TABLE IF NOT EXISTS kv (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    // ---- sessions ----

    public Session? GetByName(string name) => QuerySessions("WHERE name = $p", name).FirstOrDefault();

    public Session? GetByThread(string threadId) => QuerySessions("WHERE thread_id = $p", threadId).FirstOrDefault();

    public IReadOnlyList<Session> All() => QuerySessions("ORDER BY updated_at DESC", null);

    public void Insert(Session s) => Execute("""
        INSERT INTO sessions (name, thread_id, folder, agent, agent_session_id, status, summary, questions, created_at, updated_at)
        VALUES ($name, $thread, $folder, $agent, $sid, $status, $summary, $questions, $created, $updated)
        """,
        ("$name", s.Name), ("$thread", s.ThreadId), ("$folder", s.Folder), ("$agent", s.Agent),
        ("$sid", s.AgentSessionId), ("$status", ToDb(s.Status)), ("$summary", s.Summary), ("$questions", s.Questions),
        ("$created", s.CreatedAt.ToString("O")), ("$updated", s.UpdatedAt.ToString("O")));

    public void SetStatus(string name, SessionStatus status) => Execute(
        "UPDATE sessions SET status = $status, updated_at = $now WHERE name = $name",
        ("$status", ToDb(status)), ("$now", Now()), ("$name", name));

    public void RecordOutcome(string name, SessionStatus status, string? summary, string? questions, string? agentSessionId) => Execute("""
        UPDATE sessions
        SET status = $status, summary = $summary, questions = $questions,
            agent_session_id = COALESCE($sid, agent_session_id), updated_at = $now
        WHERE name = $name
        """,
        ("$status", ToDb(status)), ("$summary", summary), ("$questions", questions), ("$sid", agentSessionId),
        ("$now", Now()), ("$name", name));

    // ---- processed emails ----

    public bool IsProcessed(string messageId)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT 1 FROM processed_messages WHERE message_id = $id", ("$id", messageId));
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void MarkProcessed(string messageId) => Execute(
        "INSERT OR IGNORE INTO processed_messages (message_id, processed_at) VALUES ($id, $now)",
        ("$id", messageId), ("$now", Now()));

    // ---- runs ----

    public long StartRun(string session, string prompt)
    {
        lock (_lock)
        {
            using var cmd = Command(
                "INSERT INTO runs (session, started_at, prompt) VALUES ($s, $now, $p); SELECT last_insert_rowid();",
                ("$s", session), ("$now", Now()), ("$p", prompt));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void EndRun(long id, AgentResult r) => Execute("""
        UPDATE runs SET ended_at = $now, exit_code = $exit, timed_out = $to, result = $result
        WHERE id = $id
        """,
        ("$now", Now()), ("$exit", r.ExitCode), ("$to", r.TimedOut ? 1 : 0),
        ("$result", r.Text), ("$id", id));

    // ---- key/value ----

    public string? Get(string key)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT value FROM kv WHERE key = $k", ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    public void Set(string key, string value) => Execute(
        "INSERT INTO kv (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        ("$k", key), ("$v", value));

    public void Dispose() => _db.Dispose();

    // ---- helpers ----

    private static string Now() => DateTimeOffset.Now.ToString("O");

    // Stored as lowercase text ("working") so the database stays readable.
    private static string ToDb(SessionStatus status) => status.ToString().ToLowerInvariant();

    private List<Session> QuerySessions(string clause, string? param)
    {
        lock (_lock)
        {
            using var cmd = Command(
                $"SELECT name, thread_id, folder, agent, agent_session_id, status, summary, questions, created_at, updated_at FROM sessions {clause}",
                param is null ? [] : [("$p", param)]);
            using var r = cmd.ExecuteReader();
            var list = new List<Session>();
            while (r.Read())
            {
                list.Add(new Session(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), Enum.Parse<SessionStatus>(r.GetString(5), ignoreCase: true),
                    r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                    DateTimeOffset.Parse(r.GetString(8)), DateTimeOffset.Parse(r.GetString(9))));
            }
            return list;
        }
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = Command(sql, parameters);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
