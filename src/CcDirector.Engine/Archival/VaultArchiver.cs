using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Engine.Archival;

/// <summary>
/// Archives sent communications (emails, social posts) into the vault database
/// for permanent personal data storage.
/// </summary>
public sealed class VaultArchiver
{
    private readonly string _vaultDbPath;

    public VaultArchiver(string? vaultDbPath = null)
    {
        _vaultDbPath = vaultDbPath ?? CcStorage.VaultDb();
    }

    /// <summary>
    /// Archive a sent email into the vault interactions table.
    /// Returns without action if vault.db does not exist or no matching contact is found.
    /// </summary>
    public void ArchiveEmail(string to, string subject, string body, string? messageId = null)
    {
        FileLog.Write($"[VaultArchiver] ArchiveEmail: to={to}, subject={subject}");

        if (!File.Exists(_vaultDbPath))
        {
            FileLog.Write("[VaultArchiver] ArchiveEmail: vault.db not found, skipping archive");
            return;
        }

        using var conn = new SqliteConnection($"Data Source={_vaultDbPath};Mode=ReadWrite");
        conn.Open();

        var contactId = FindContactByEmail(conn, to);

        if (contactId == null)
        {
            FileLog.Write($"[VaultArchiver] ArchiveEmail: no contact found for {to}, skipping vault archive");
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO interactions (contact_id, type, direction, subject, content, message_id, interaction_date)
            VALUES (@contactId, 'email', 'outbound', @subject, @body, @messageId, @now)
            """;
        cmd.Parameters.AddWithValue("@contactId", contactId.Value);
        cmd.Parameters.AddWithValue("@subject", subject);
        cmd.Parameters.AddWithValue("@body", body);
        cmd.Parameters.AddWithValue("@messageId", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        FileLog.Write($"[VaultArchiver] ArchiveEmail: archived email to {to}");
    }

    /// <summary>
    /// Archive a social media post into the vault social_posts table.
    /// Returns without action if vault.db does not exist.
    /// </summary>
    public void ArchiveSocialPost(string platform, string content, string? url = null)
    {
        FileLog.Write($"[VaultArchiver] ArchiveSocialPost: platform={platform}");

        if (!File.Exists(_vaultDbPath))
        {
            FileLog.Write("[VaultArchiver] ArchiveSocialPost: vault.db not found, skipping archive");
            return;
        }

        using var conn = new SqliteConnection($"Data Source={_vaultDbPath};Mode=ReadWrite");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO social_posts (platform, content, status, url, posted_at)
            VALUES (@platform, @content, 'posted', @url, @now)
            """;
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@url", (object?)url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        FileLog.Write($"[VaultArchiver] ArchiveSocialPost: archived {platform} post");
    }

    private static long? FindContactByEmail(SqliteConnection conn, string email)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM contacts WHERE email = @email LIMIT 1";
        cmd.Parameters.AddWithValue("@email", email);
        var result = cmd.ExecuteScalar();
        return result is long id ? id : null;
    }
}
