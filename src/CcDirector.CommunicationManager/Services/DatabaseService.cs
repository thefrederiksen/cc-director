using System.IO;
using System.Text.Json;
using CommunicationManager.Models;
using Microsoft.Data.Sqlite;

namespace CommunicationManager.Services;

public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly string _contentPath;
    private readonly string _tempMediaPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public string ContentPath => _contentPath;

    public DatabaseService(string contentPath)
    {
        _contentPath = contentPath;
        _tempMediaPath = Path.Combine(Path.GetTempPath(), "comm_manager_media");
        var dbPath = Path.Combine(contentPath, "communications.db");
        _connectionString = $"Data Source={dbPath}";

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Performs async initialization: creates directories and database schema.
    /// Must be called after construction before any other operations.
    /// </summary>
    public async Task InitializeAsync()
    {
        System.Diagnostics.Debug.WriteLine("[DatabaseService] InitializeAsync: creating directories and schema");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(_contentPath);
            Directory.CreateDirectory(_tempMediaPath);
        });

        await InitializeSchemaAsync();
    }

    private async Task InitializeSchemaAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS communications (
                id TEXT PRIMARY KEY,
                ticket_number INTEGER UNIQUE NOT NULL,
                platform TEXT NOT NULL,
                type TEXT NOT NULL,
                persona TEXT NOT NULL,
                persona_display TEXT,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT DEFAULT 'claude_code',
                posted_at TEXT,
                posted_by TEXT,
                posted_url TEXT,
                post_id TEXT,
                rejected_at TEXT,
                rejected_by TEXT,
                rejection_reason TEXT,
                scheduled_for TEXT,
                status TEXT NOT NULL DEFAULT 'pending_review',
                send_timing TEXT DEFAULT 'asap',
                send_from TEXT,
                context_url TEXT,
                context_title TEXT,
                context_author TEXT,
                destination_url TEXT,
                campaign_id TEXT,
                notes TEXT,
                tags TEXT,
                linkedin_specific TEXT,
                twitter_specific TEXT,
                reddit_specific TEXT,
                email_specific TEXT,
                article_specific TEXT,
                recipient TEXT,
                thread_content TEXT
            );

            CREATE TABLE IF NOT EXISTS media (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                communication_id TEXT NOT NULL REFERENCES communications(id) ON DELETE CASCADE,
                type TEXT NOT NULL,
                filename TEXT NOT NULL,
                data BLOB NOT NULL,
                alt_text TEXT,
                file_size INTEGER,
                mime_type TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_status ON communications(status);
            CREATE INDEX IF NOT EXISTS idx_platform ON communications(platform);
            CREATE INDEX IF NOT EXISTS idx_created_at ON communications(created_at);
            CREATE INDEX IF NOT EXISTS idx_posted_at ON communications(posted_at);
            CREATE INDEX IF NOT EXISTS idx_ticket_number ON communications(ticket_number);
            CREATE INDEX IF NOT EXISTS idx_media_comm_id ON media(communication_id);
        ";
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<ContentItem>> LoadItemsByStatusAsync(string status)
    {
        var items = new List<ContentItem>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT * FROM communications
            WHERE status = $status
            ORDER BY created_at DESC";
        command.Parameters.AddWithValue("$status", status);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var item = MapRowToContentItem(reader);
            items.Add(item);
        }

        // Load media for each item
        foreach (var item in items)
        {
            item.Media = await LoadMediaForItemAsync(connection, item.Id);
        }

        return items;
    }

    public async Task<List<ContentItem>> LoadAllItemsAsync()
    {
        var items = new List<ContentItem>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM communications ORDER BY created_at DESC";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var item = MapRowToContentItem(reader);
            items.Add(item);
        }

        // Load media for each item
        foreach (var item in items)
        {
            item.Media = await LoadMediaForItemAsync(connection, item.Id);
        }

        return items;
    }

    private async Task<List<MediaItem>> LoadMediaForItemAsync(SqliteConnection connection, string communicationId)
    {
        var mediaItems = new List<MediaItem>();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, type, filename, data, alt_text, file_size, mime_type
            FROM media
            WHERE communication_id = $id";
        command.Parameters.AddWithValue("$id", communicationId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var mediaId = reader.GetInt32(reader.GetOrdinal("id"));
            var filename = reader.IsDBNull(reader.GetOrdinal("filename")) ? $"media_{mediaId}" : reader.GetString(reader.GetOrdinal("filename"));
            var data = reader.GetFieldValue<byte[]>(reader.GetOrdinal("data"));

            // Extract to temp file for UI display
            var tempPath = Path.Combine(_tempMediaPath, $"{mediaId}_{filename}");
            if (!File.Exists(tempPath))
            {
                await File.WriteAllBytesAsync(tempPath, data);
            }

            mediaItems.Add(new MediaItem
            {
                Id = mediaId,
                Type = reader.GetString(reader.GetOrdinal("type")),
                Filename = filename,
                AltText = reader.IsDBNull(reader.GetOrdinal("alt_text")) ? null : reader.GetString(reader.GetOrdinal("alt_text")),
                FileSize = reader.IsDBNull(reader.GetOrdinal("file_size")) ? null : reader.GetInt64(reader.GetOrdinal("file_size")),
                MimeType = reader.IsDBNull(reader.GetOrdinal("mime_type")) ? null : reader.GetString(reader.GetOrdinal("mime_type")),
                TempPath = tempPath
            });
        }

        return mediaItems;
    }

    public async Task<ContentItem?> GetByTicketNumberAsync(int ticketNumber)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM communications WHERE ticket_number = $ticket";
        command.Parameters.AddWithValue("$ticket", ticketNumber);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var item = MapRowToContentItem(reader);
            item.Media = await LoadMediaForItemAsync(connection, item.Id);
            return item;
        }

        return null;
    }

    public async Task<bool> UpdateStatusAsync(int ticketNumber, string newStatus, Dictionary<string, object?>? additionalFields = null)
    {
        System.Diagnostics.Debug.WriteLine($"[DatabaseService] UpdateStatusAsync: ticket={ticketNumber}, newStatus={newStatus}");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var fields = new List<string> { "status = $status" };
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$status", newStatus);
        command.Parameters.AddWithValue("$ticket", ticketNumber);

        if (additionalFields != null)
        {
            foreach (var kvp in additionalFields)
            {
                var paramName = $"${kvp.Key}";
                fields.Add($"{kvp.Key} = {paramName}");
                command.Parameters.AddWithValue(paramName, kvp.Value ?? DBNull.Value);
            }
        }

        command.CommandText = $"UPDATE communications SET {string.Join(", ", fields)} WHERE ticket_number = $ticket";
        var rows = await command.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<bool> UpdateContentAsync(int ticketNumber, string content)
    {
        System.Diagnostics.Debug.WriteLine($"[DatabaseService] UpdateContentAsync: ticket={ticketNumber}");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE communications SET content = $content WHERE ticket_number = $ticket";
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$ticket", ticketNumber);

        var rows = await command.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(int ticketNumber)
    {
        System.Diagnostics.Debug.WriteLine($"[DatabaseService] DeleteAsync: ticket={ticketNumber}");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Media records cascade deleted via foreign key
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM communications WHERE ticket_number = $ticket";
        command.Parameters.AddWithValue("$ticket", ticketNumber);

        var rows = await command.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<Dictionary<string, int>> GetStatsAsync()
    {
        var stats = new Dictionary<string, int>
        {
            { "pending_review", 0 },
            { "approved", 0 },
            { "rejected", 0 },
            { "posted", 0 }
        };

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT status, COUNT(*) as count FROM communications GROUP BY status";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var status = reader.GetString(0);
            var count = reader.GetInt32(1);
            if (stats.ContainsKey(status))
            {
                stats[status] = count;
            }
        }

        return stats;
    }

    private ContentItem MapRowToContentItem(SqliteDataReader reader)
    {
        var item = new ContentItem
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            TicketNumber = reader.GetInt32(reader.GetOrdinal("ticket_number")),
            Platform = reader.GetString(reader.GetOrdinal("platform")),
            Type = reader.GetString(reader.GetOrdinal("type")),
            Persona = reader.GetString(reader.GetOrdinal("persona")),
            PersonaDisplay = GetNullableString(reader, "persona_display") ?? "",
            Content = reader.GetString(reader.GetOrdinal("content")),
            CreatedBy = GetNullableString(reader, "created_by") ?? "claude_code",
            Status = reader.GetString(reader.GetOrdinal("status")),
            SendTiming = GetNullableString(reader, "send_timing") ?? "asap",
            SendFrom = GetNullableString(reader, "send_from"),
            ContextUrl = GetNullableString(reader, "context_url"),
            ContextTitle = GetNullableString(reader, "context_title"),
            ContextAuthor = GetNullableString(reader, "context_author"),
            DestinationUrl = GetNullableString(reader, "destination_url"),
            CampaignId = GetNullableString(reader, "campaign_id"),
            Notes = GetNullableString(reader, "notes"),
            RejectionReason = GetNullableString(reader, "rejection_reason"),
            RejectedBy = GetNullableString(reader, "rejected_by"),
            PostedBy = GetNullableString(reader, "posted_by"),
            PostedUrl = GetNullableString(reader, "posted_url"),
            PostId = GetNullableString(reader, "post_id")
        };

        // Parse datetime fields
        var createdAt = GetNullableString(reader, "created_at");
        if (!string.IsNullOrEmpty(createdAt) && DateTime.TryParse(createdAt, out var created))
        {
            item.CreatedAt = created;
        }

        var postedAt = GetNullableString(reader, "posted_at");
        if (!string.IsNullOrEmpty(postedAt) && DateTime.TryParse(postedAt, out var posted))
        {
            item.PostedAt = posted;
        }

        var rejectedAt = GetNullableString(reader, "rejected_at");
        if (!string.IsNullOrEmpty(rejectedAt) && DateTime.TryParse(rejectedAt, out var rejected))
        {
            item.RejectedAt = rejected;
        }

        var scheduledFor = GetNullableString(reader, "scheduled_for");
        if (!string.IsNullOrEmpty(scheduledFor) && DateTime.TryParse(scheduledFor, out var scheduled))
        {
            item.ScheduledFor = scheduled;
        }

        // Parse JSON fields
        var tags = GetNullableString(reader, "tags");
        if (!string.IsNullOrEmpty(tags))
        {
            try
            {
                item.Tags = JsonSerializer.Deserialize<List<string>>(tags, _jsonOptions);
            }
            catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"[DatabaseService] Tags JSON parse error: {ex.Message}"); }
        }

        var linkedIn = GetNullableString(reader, "linkedin_specific");
        if (!string.IsNullOrEmpty(linkedIn))
        {
            try
            {
                item.LinkedInSpecific = JsonSerializer.Deserialize<LinkedInSpecific>(linkedIn, _jsonOptions);
            }
            catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"[DatabaseService] LinkedInSpecific JSON parse error: {ex.Message}"); }
        }

        var twitter = GetNullableString(reader, "twitter_specific");
        if (!string.IsNullOrEmpty(twitter))
        {
            try
            {
                item.TwitterSpecific = JsonSerializer.Deserialize<TwitterSpecific>(twitter, _jsonOptions);
            }
            catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"[DatabaseService] TwitterSpecific JSON parse error: {ex.Message}"); }
        }

        var reddit = GetNullableString(reader, "reddit_specific");
        if (!string.IsNullOrEmpty(reddit))
        {
            try
            {
                item.RedditSpecific = JsonSerializer.Deserialize<RedditSpecific>(reddit, _jsonOptions);
            }
            catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"[DatabaseService] RedditSpecific JSON parse error: {ex.Message}"); }
        }

        var email = GetNullableString(reader, "email_specific");
        if (!string.IsNullOrEmpty(email))
        {
            try
            {
                item.EmailSpecific = JsonSerializer.Deserialize<EmailSpecific>(email, _jsonOptions);
            }
            catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"[DatabaseService] EmailSpecific JSON parse error: {ex.Message}"); }
        }

        var article = GetNullableString(reader, "article_specific");
        if (!string.IsNullOrEmpty(article))
        {
            try
            {
                item.ArticleSpecific = JsonSerializer.Deserialize<ArticleSpecific>(article, _jsonOptions);
            }
            catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"[DatabaseService] ArticleSpecific JSON parse error: {ex.Message}"); }
        }

        var recipient = GetNullableString(reader, "recipient");
        if (!string.IsNullOrEmpty(recipient))
        {
            try
            {
                item.Recipient = JsonSerializer.Deserialize<RecipientInfo>(recipient, _jsonOptions);
            }
            catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"[DatabaseService] Recipient JSON parse error: {ex.Message}"); }
        }

        var threadContent = GetNullableString(reader, "thread_content");
        if (!string.IsNullOrEmpty(threadContent))
        {
            try
            {
                item.ThreadContent = JsonSerializer.Deserialize<List<string>>(threadContent, _jsonOptions);
            }
            catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"[DatabaseService] ThreadContent JSON parse error: {ex.Message}"); }
        }

        return item;
    }

    private static string? GetNullableString(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>
    /// Retrieves media BLOB data by ID.
    /// </summary>
    public async Task<byte[]?> GetMediaDataAsync(int mediaId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM media WHERE id = $id";
        command.Parameters.AddWithValue("$id", mediaId);

        var result = await command.ExecuteScalarAsync();
        return result as byte[];
    }

    /// <summary>
    /// Extracts media BLOB to a temp file for UI display.
    /// Returns the temp file path.
    /// </summary>
    public async Task<string?> ExtractMediaToTempAsync(int mediaId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT filename, data FROM media WHERE id = $id";
        command.Parameters.AddWithValue("$id", mediaId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var filename = reader.GetString(0);
        var data = reader.GetFieldValue<byte[]>(1);

        // Create temp file with unique name to avoid collisions
        var tempPath = Path.Combine(_tempMediaPath, $"{mediaId}_{filename}");

        await File.WriteAllBytesAsync(tempPath, data);
        return tempPath;
    }

    /// <summary>
    /// Extracts media BLOB to a temp file synchronously for UI display.
    /// Returns the temp file path.
    /// </summary>
    public string? ExtractMediaToTemp(int mediaId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT filename, data FROM media WHERE id = $id";
        command.Parameters.AddWithValue("$id", mediaId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var filename = reader.GetString(0);
        var data = reader.GetFieldValue<byte[]>(1);

        // Create temp file with unique name to avoid collisions
        var tempPath = Path.Combine(_tempMediaPath, $"{mediaId}_{filename}");

        File.WriteAllBytes(tempPath, data);
        return tempPath;
    }

    /// <summary>
    /// Cleans up temp media files older than specified age.
    /// </summary>
    public void CleanupTempMedia(TimeSpan maxAge)
    {
        if (!Directory.Exists(_tempMediaPath))
            return;

        var cutoff = DateTime.Now - maxAge;
        foreach (var file in Directory.GetFiles(_tempMediaPath))
        {
            try
            {
                if (File.GetLastAccessTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] Cleanup error for {file}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // No persistent connection to dispose
    }
}
