using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Core.QuickActions;

/// <summary>
/// SQLite database for Quick Actions threads and messages.
/// Stores conversation history for the ChatGPT-like quick action interface.
/// </summary>
public sealed class QuickActionDatabase
{
    private readonly string _connectionString;

    public QuickActionDatabase(string databasePath)
    {
        FileLog.Write($"[QuickActionDatabase] Creating: path={databasePath}");

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={databasePath}";
        InitializeSchema();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    private void InitializeSchema()
    {
        FileLog.Write("[QuickActionDatabase] InitializeSchema: creating tables if needed");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS threads (
                id         TEXT PRIMARY KEY,
                title      TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS messages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                thread_id  TEXT NOT NULL,
                role       TEXT NOT NULL,
                content    TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (thread_id) REFERENCES threads(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_messages_thread_id ON messages(thread_id);
            CREATE INDEX IF NOT EXISTS idx_threads_updated_at ON threads(updated_at);
            """;
        cmd.ExecuteNonQuery();

        FileLog.Write("[QuickActionDatabase] InitializeSchema: complete");
    }

    // -- Threads --

    public QuickActionThread CreateThread(string title)
    {
        FileLog.Write($"[QuickActionDatabase] CreateThread: title={title}");

        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO threads (id, title, created_at, updated_at)
            VALUES (@id, @title, @now, @now)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();

        var thread = new QuickActionThread
        {
            Id = id,
            Title = title,
            CreatedAt = DateTime.Parse(now),
            UpdatedAt = DateTime.Parse(now)
        };

        FileLog.Write($"[QuickActionDatabase] CreateThread: id={id}");
        return thread;
    }

    public List<QuickActionThread> GetThreads()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM threads ORDER BY updated_at DESC";

        using var reader = cmd.ExecuteReader();
        var threads = new List<QuickActionThread>();
        while (reader.Read())
            threads.Add(ReadThreadRecord(reader));
        return threads;
    }

    public QuickActionThread? GetThread(string threadId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM threads WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", threadId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadThreadRecord(reader) : null;
    }

    public void RenameThread(string threadId, string newTitle)
    {
        FileLog.Write($"[QuickActionDatabase] RenameThread: id={threadId}, newTitle={newTitle}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE threads SET title = @title, updated_at = datetime('now') WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", threadId);
        cmd.Parameters.AddWithValue("@title", newTitle);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteThread(string threadId)
    {
        FileLog.Write($"[QuickActionDatabase] DeleteThread: id={threadId}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM threads WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", threadId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void TouchThread(string threadId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE threads SET updated_at = datetime('now') WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", threadId);
        cmd.ExecuteNonQuery();
    }

    // -- Messages --

    public int AddMessage(string threadId, string role, string content)
    {
        FileLog.Write($"[QuickActionDatabase] AddMessage: threadId={threadId}, role={role}, contentLen={content.Length}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (thread_id, role, content)
            VALUES (@threadId, @role, @content);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@threadId", threadId);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@content", content);

        var id = Convert.ToInt32(cmd.ExecuteScalar());

        // Update thread timestamp
        TouchThread(threadId);

        FileLog.Write($"[QuickActionDatabase] AddMessage: id={id}");
        return id;
    }

    public List<QuickActionMessage> GetMessages(string threadId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE thread_id = @threadId ORDER BY id ASC";
        cmd.Parameters.AddWithValue("@threadId", threadId);

        using var reader = cmd.ExecuteReader();
        var messages = new List<QuickActionMessage>();
        while (reader.Read())
            messages.Add(ReadMessageRecord(reader));
        return messages;
    }

    public QuickActionMessage? GetLastMessage(string threadId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE thread_id = @threadId ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@threadId", threadId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMessageRecord(reader) : null;
    }

    // -- Readers --

    private static QuickActionThread ReadThreadRecord(SqliteDataReader reader)
    {
        return new QuickActionThread
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    private static QuickActionMessage ReadMessageRecord(SqliteDataReader reader)
    {
        return new QuickActionMessage
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            ThreadId = reader.GetString(reader.GetOrdinal("thread_id")),
            Role = reader.GetString(reader.GetOrdinal("role")),
            Content = reader.GetString(reader.GetOrdinal("content")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")))
        };
    }
}
