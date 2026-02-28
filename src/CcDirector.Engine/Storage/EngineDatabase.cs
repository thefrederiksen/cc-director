using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Engine.Storage;

public sealed class EngineDatabase
{
    private readonly string _connectionString;

    public EngineDatabase(string databasePath)
    {
        FileLog.Write($"[EngineDatabase] Creating: path={databasePath}");

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
        FileLog.Write("[EngineDatabase] InitializeSchema: creating tables if needed");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT UNIQUE NOT NULL,
                cron            TEXT NOT NULL,
                command         TEXT NOT NULL,
                working_dir     TEXT,
                enabled         INTEGER DEFAULT 1,
                timeout_seconds INTEGER DEFAULT 300,
                tags            TEXT,
                created_at      TEXT DEFAULT (datetime('now')),
                updated_at      TEXT DEFAULT (datetime('now')),
                next_run        TEXT
            );

            CREATE TABLE IF NOT EXISTS runs (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id           INTEGER NOT NULL,
                job_name         TEXT NOT NULL,
                started_at       TEXT NOT NULL,
                ended_at         TEXT,
                exit_code        INTEGER,
                stdout           TEXT,
                stderr           TEXT,
                timed_out        INTEGER DEFAULT 0,
                duration_seconds REAL,
                FOREIGN KEY (job_id) REFERENCES jobs(id)
            );

            CREATE TABLE IF NOT EXISTS communications (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                ticket_number    TEXT UNIQUE NOT NULL,
                platform         TEXT NOT NULL,
                type             TEXT,
                status           TEXT NOT NULL DEFAULT 'pending_review',
                subject          TEXT,
                body             TEXT,
                send_from        TEXT,
                persona          TEXT,
                recipient        TEXT,
                email_specific   TEXT,
                linkedin_specific TEXT,
                send_timing      TEXT DEFAULT 'immediate',
                scheduled_for    TEXT,
                created_at       TEXT DEFAULT (datetime('now')),
                approved_at      TEXT,
                posted_at        TEXT,
                posted_by        TEXT,
                tags             TEXT
            );

            CREATE TABLE IF NOT EXISTS media (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                communication_id INTEGER NOT NULL,
                type             TEXT,
                filename         TEXT,
                alt_text         TEXT,
                file_size        INTEGER,
                mime_type        TEXT,
                data             BLOB,
                FOREIGN KEY (communication_id) REFERENCES communications(id)
            );

            CREATE INDEX IF NOT EXISTS idx_runs_job_id ON runs(job_id);
            CREATE INDEX IF NOT EXISTS idx_runs_started_at ON runs(started_at);
            CREATE INDEX IF NOT EXISTS idx_jobs_next_run ON jobs(next_run);
            CREATE INDEX IF NOT EXISTS idx_jobs_enabled ON jobs(enabled);
            CREATE INDEX IF NOT EXISTS idx_comms_status ON communications(status);
            CREATE INDEX IF NOT EXISTS idx_comms_timing ON communications(send_timing);
            """;
        cmd.ExecuteNonQuery();

        FileLog.Write("[EngineDatabase] InitializeSchema: complete");
    }

    // -- Jobs --

    public int AddJob(JobRecord job)
    {
        FileLog.Write($"[EngineDatabase] AddJob: name={job.Name}, cron={job.Cron}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (name, cron, command, working_dir, enabled, timeout_seconds, tags, next_run)
            VALUES (@name, @cron, @command, @workingDir, @enabled, @timeout, @tags, @nextRun);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@name", job.Name);
        cmd.Parameters.AddWithValue("@cron", job.Cron);
        cmd.Parameters.AddWithValue("@command", job.Command);
        cmd.Parameters.AddWithValue("@workingDir", (object?)job.WorkingDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", job.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@timeout", job.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@tags", (object?)job.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nextRun", job.NextRun.HasValue ? job.NextRun.Value.ToString("o") : DBNull.Value);

        var id = Convert.ToInt32(cmd.ExecuteScalar());
        FileLog.Write($"[EngineDatabase] AddJob: id={id}");
        return id;
    }

    public JobRecord? GetJob(string name)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJobRecord(reader) : null;
    }

    public JobRecord? GetJobById(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJobRecord(reader) : null;
    }

    public List<JobRecord> ListJobs(bool includeDisabled = false, string? tag = null)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!includeDisabled)
            where.Add("enabled = 1");
        if (tag != null)
        {
            where.Add("tags LIKE @tag");
            cmd.Parameters.AddWithValue("@tag", $"%{tag}%");
        }

        cmd.CommandText = "SELECT * FROM jobs"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY name";

        using var reader = cmd.ExecuteReader();
        var jobs = new List<JobRecord>();
        while (reader.Read())
            jobs.Add(ReadJobRecord(reader));
        return jobs;
    }

    public void UpdateJob(JobRecord job)
    {
        FileLog.Write($"[EngineDatabase] UpdateJob: id={job.Id}, name={job.Name}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE jobs SET
                name = @name,
                cron = @cron,
                command = @command,
                working_dir = @workingDir,
                enabled = @enabled,
                timeout_seconds = @timeout,
                tags = @tags,
                next_run = @nextRun,
                updated_at = datetime('now')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", job.Id);
        cmd.Parameters.AddWithValue("@name", job.Name);
        cmd.Parameters.AddWithValue("@cron", job.Cron);
        cmd.Parameters.AddWithValue("@command", job.Command);
        cmd.Parameters.AddWithValue("@workingDir", (object?)job.WorkingDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", job.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@timeout", job.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@tags", (object?)job.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nextRun", job.NextRun.HasValue ? job.NextRun.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteJob(string name)
    {
        FileLog.Write($"[EngineDatabase] DeleteJob: name={name}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetJobEnabled(string name, bool enabled)
    {
        FileLog.Write($"[EngineDatabase] SetJobEnabled: name={name}, enabled={enabled}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET enabled = @enabled, updated_at = datetime('now') WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void UpdateNextRun(int jobId, DateTime? nextRun)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET next_run = @nextRun WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", jobId);
        cmd.Parameters.AddWithValue("@nextRun", nextRun.HasValue ? nextRun.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<JobRecord> GetDueJobs()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM jobs
            WHERE enabled = 1
              AND next_run IS NOT NULL
              AND next_run <= @now
            """;
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));

        using var reader = cmd.ExecuteReader();
        var jobs = new List<JobRecord>();
        while (reader.Read())
            jobs.Add(ReadJobRecord(reader));
        return jobs;
    }

    // -- Runs --

    public int CreateRun(RunRecord run)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs (job_id, job_name, started_at)
            VALUES (@jobId, @jobName, @startedAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@jobId", run.JobId);
        cmd.Parameters.AddWithValue("@jobName", run.JobName);
        cmd.Parameters.AddWithValue("@startedAt", run.StartedAt.ToString("o"));

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateRun(RunRecord run)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET
                ended_at = @endedAt,
                exit_code = @exitCode,
                stdout = @stdout,
                stderr = @stderr,
                timed_out = @timedOut,
                duration_seconds = @duration
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", run.Id);
        cmd.Parameters.AddWithValue("@endedAt", run.EndedAt.HasValue ? run.EndedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("@exitCode", (object?)run.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stdout", (object?)run.Stdout ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stderr", (object?)run.Stderr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timedOut", run.TimedOut ? 1 : 0);
        cmd.Parameters.AddWithValue("@duration", (object?)run.DurationSeconds ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public RunRecord? GetRun(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM runs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRunRecord(reader) : null;
    }

    public List<RunRecord> ListRuns(string? jobName = null, int limit = 50, bool failedOnly = false)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (jobName != null)
        {
            where.Add("job_name = @jobName");
            cmd.Parameters.AddWithValue("@jobName", jobName);
        }
        if (failedOnly)
            where.Add("(exit_code IS NOT NULL AND exit_code != 0) OR timed_out = 1");

        cmd.CommandText = "SELECT * FROM runs"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY started_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        var runs = new List<RunRecord>();
        while (reader.Read())
            runs.Add(ReadRunRecord(reader));
        return runs;
    }

    public int CleanupOrphanedRuns()
    {
        FileLog.Write("[EngineDatabase] CleanupOrphanedRuns: marking interrupted runs as failed");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET
                ended_at = datetime('now'),
                exit_code = -1,
                stderr = 'Interrupted by shutdown',
                duration_seconds = 0
            WHERE ended_at IS NULL
            """;
        var count = cmd.ExecuteNonQuery();

        FileLog.Write($"[EngineDatabase] CleanupOrphanedRuns: marked {count} runs as failed");
        return count;
    }

    public int CleanupOldRuns(int retentionDays)
    {
        FileLog.Write($"[EngineDatabase] CleanupOldRuns: purging runs older than {retentionDays} days");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM runs WHERE started_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-retentionDays).ToString("o"));
        var count = cmd.ExecuteNonQuery();

        FileLog.Write($"[EngineDatabase] CleanupOldRuns: purged {count} runs");
        return count;
    }

    // -- Readers --

    private static JobRecord ReadJobRecord(SqliteDataReader reader)
    {
        return new JobRecord
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Cron = reader.GetString(reader.GetOrdinal("cron")),
            Command = reader.GetString(reader.GetOrdinal("command")),
            WorkingDir = reader.IsDBNull(reader.GetOrdinal("working_dir")) ? null : reader.GetString(reader.GetOrdinal("working_dir")),
            Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
            TimeoutSeconds = reader.GetInt32(reader.GetOrdinal("timeout_seconds")),
            Tags = reader.IsDBNull(reader.GetOrdinal("tags")) ? null : reader.GetString(reader.GetOrdinal("tags")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            NextRun = reader.IsDBNull(reader.GetOrdinal("next_run")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("next_run")))
        };
    }

    private static RunRecord ReadRunRecord(SqliteDataReader reader)
    {
        return new RunRecord
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            JobId = reader.GetInt32(reader.GetOrdinal("job_id")),
            JobName = reader.GetString(reader.GetOrdinal("job_name")),
            StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
            EndedAt = reader.IsDBNull(reader.GetOrdinal("ended_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("ended_at"))),
            ExitCode = reader.IsDBNull(reader.GetOrdinal("exit_code")) ? null : reader.GetInt32(reader.GetOrdinal("exit_code")),
            Stdout = reader.IsDBNull(reader.GetOrdinal("stdout")) ? null : reader.GetString(reader.GetOrdinal("stdout")),
            Stderr = reader.IsDBNull(reader.GetOrdinal("stderr")) ? null : reader.GetString(reader.GetOrdinal("stderr")),
            TimedOut = reader.GetInt32(reader.GetOrdinal("timed_out")) == 1,
            DurationSeconds = reader.IsDBNull(reader.GetOrdinal("duration_seconds")) ? null : reader.GetDouble(reader.GetOrdinal("duration_seconds"))
        };
    }
}
