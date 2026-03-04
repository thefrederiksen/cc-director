using System.IO;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.DocumentLibrary.Models;
using Microsoft.Data.Sqlite;

namespace CcDirector.DocumentLibrary.Services;

/// <summary>
/// Direct SQLite reader for the vault catalog database.
/// Read-only -- all writes go through cc-vault CLI to avoid conflicts.
/// </summary>
public sealed class CatalogDatabase
{
    private string ConnectionString
    {
        get
        {
            var dbPath = CcStorage.VaultDb();
            return $"Data Source={dbPath};Mode=ReadOnly";
        }
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>List all registered libraries with stats.</summary>
    public List<Library> ListLibraries()
    {
        FileLog.Write("[CatalogDatabase] ListLibraries");
        var libs = new List<Library>();

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM libraries ORDER BY label";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            libs.Add(ReadLibrary(reader));
        }

        // Attach stats to each library
        foreach (var lib in libs)
        {
            lib.Stats = GetStats(conn, lib.Id);
        }

        FileLog.Write($"[CatalogDatabase] ListLibraries: {libs.Count} libraries");
        return libs;
    }

    /// <summary>Get distinct departments for a library.</summary>
    public List<string> GetDepartments(int libraryId)
    {
        FileLog.Write($"[CatalogDatabase] GetDepartments: libraryId={libraryId}");
        var departments = new List<string>();

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT department FROM catalog_entries
            WHERE library_id = @libId AND department IS NOT NULL
            ORDER BY department
            """;
        cmd.Parameters.AddWithValue("@libId", libraryId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            departments.Add(reader.GetString(0));
        }

        FileLog.Write($"[CatalogDatabase] GetDepartments: {departments.Count} departments");
        return departments;
    }

    /// <summary>List catalog entries with filters, sorting, and pagination.</summary>
    public List<CatalogEntry> ListEntries(
        int? libraryId = null,
        string? department = null,
        string? ext = null,
        string? status = null,
        string sortColumn = "file_name",
        bool sortAscending = true,
        int offset = 0,
        int limit = 200)
    {
        FileLog.Write($"[CatalogDatabase] ListEntries: lib={libraryId}, dept={department}, ext={ext}, sort={sortColumn}, offset={offset}, limit={limit}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (libraryId.HasValue)
        {
            where.Add("library_id = @libId");
            cmd.Parameters.AddWithValue("@libId", libraryId.Value);
        }
        if (!string.IsNullOrEmpty(department))
        {
            where.Add("department = @dept");
            cmd.Parameters.AddWithValue("@dept", department);
        }
        if (!string.IsNullOrEmpty(ext))
        {
            where.Add("file_ext = @ext");
            cmd.Parameters.AddWithValue("@ext", ext);
        }
        if (!string.IsNullOrEmpty(status))
        {
            where.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", status);
        }

        // Whitelist sort columns to prevent injection
        var validSortColumns = new HashSet<string>
        {
            "file_name", "file_ext", "file_size", "file_modified_at",
            "status", "department", "title", "created_at"
        };
        // Safe: sortColumn is validated against whitelist above, direction is a bool-derived constant
        if (!validSortColumns.Contains(sortColumn))
            sortColumn = "file_name";

        var direction = sortAscending ? "ASC" : "DESC";
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT * FROM catalog_entries
            {whereClause}
            ORDER BY {sortColumn} {direction}
            LIMIT @limit OFFSET @offset
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var entries = new List<CatalogEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        FileLog.Write($"[CatalogDatabase] ListEntries: {entries.Count} entries");
        return entries;
    }

    /// <summary>Full-text search across catalog entries.</summary>
    public List<CatalogEntry> Search(string query, int limit = 50)
    {
        FileLog.Write($"[CatalogDatabase] Search: {query}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ce.* FROM catalog_fts
            JOIN catalog_entries ce ON ce.id = catalog_fts.rowid
            WHERE catalog_fts MATCH @query
            ORDER BY bm25(catalog_fts)
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@limit", limit);

        var entries = new List<CatalogEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        FileLog.Write($"[CatalogDatabase] Search: {entries.Count} results");
        return entries;
    }

    /// <summary>Get total entry count for a library (with optional filters).</summary>
    public int GetEntryCount(int? libraryId = null, string? department = null, string? ext = null)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (libraryId.HasValue)
        {
            where.Add("library_id = @libId");
            cmd.Parameters.AddWithValue("@libId", libraryId.Value);
        }
        if (!string.IsNullOrEmpty(department))
        {
            where.Add("department = @dept");
            cmd.Parameters.AddWithValue("@dept", department);
        }
        if (!string.IsNullOrEmpty(ext))
        {
            where.Add("file_ext = @ext");
            cmd.Parameters.AddWithValue("@ext", ext);
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"SELECT COUNT(*) FROM catalog_entries {whereClause}";

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private CatalogStats GetStats(SqliteConnection conn, int libraryId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN status = 'summarized' THEN 1 ELSE 0 END) AS summarized,
                SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END) AS pending,
                SUM(CASE WHEN status = 'error' THEN 1 ELSE 0 END) AS errors,
                SUM(CASE WHEN status = 'skipped' THEN 1 ELSE 0 END) AS skipped,
                SUM(CASE WHEN status = 'missing' THEN 1 ELSE 0 END) AS missing
            FROM catalog_entries WHERE library_id = @libId
            """;
        cmd.Parameters.AddWithValue("@libId", libraryId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new CatalogStats();

        return new CatalogStats
        {
            Total = reader.GetInt32(reader.GetOrdinal("total")),
            Summarized = reader.GetInt32(reader.GetOrdinal("summarized")),
            Pending = reader.GetInt32(reader.GetOrdinal("pending")),
            Errors = reader.GetInt32(reader.GetOrdinal("errors")),
            Skipped = reader.GetInt32(reader.GetOrdinal("skipped")),
            Missing = reader.GetInt32(reader.GetOrdinal("missing")),
        };
    }

    private static Library ReadLibrary(SqliteDataReader reader)
    {
        return new Library
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Path = reader.GetString(reader.GetOrdinal("path")),
            Label = reader.GetString(reader.GetOrdinal("label")),
            Category = reader.GetString(reader.GetOrdinal("category")),
            Owner = reader.IsDBNull(reader.GetOrdinal("owner")) ? null : reader.GetString(reader.GetOrdinal("owner")),
            Recursive = reader.GetInt32(reader.GetOrdinal("recursive")),
            Enabled = reader.GetInt32(reader.GetOrdinal("enabled")),
            LastScanned = reader.IsDBNull(reader.GetOrdinal("last_scanned")) ? null : reader.GetString(reader.GetOrdinal("last_scanned")),
            CreatedAt = reader.IsDBNull(reader.GetOrdinal("created_at")) ? null : reader.GetString(reader.GetOrdinal("created_at")),
        };
    }

    private static CatalogEntry ReadEntry(SqliteDataReader reader)
    {
        return new CatalogEntry
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            LibraryId = reader.GetInt32(reader.GetOrdinal("library_id")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            FileExt = reader.GetString(reader.GetOrdinal("file_ext")),
            FileSize = reader.IsDBNull(reader.GetOrdinal("file_size")) ? 0 : reader.GetInt64(reader.GetOrdinal("file_size")),
            FileHash = reader.IsDBNull(reader.GetOrdinal("file_hash")) ? null : reader.GetString(reader.GetOrdinal("file_hash")),
            FileModifiedAt = reader.IsDBNull(reader.GetOrdinal("file_modified_at")) ? null : reader.GetString(reader.GetOrdinal("file_modified_at")),
            Title = reader.IsDBNull(reader.GetOrdinal("title")) ? null : reader.GetString(reader.GetOrdinal("title")),
            Summary = reader.IsDBNull(reader.GetOrdinal("summary")) ? null : reader.GetString(reader.GetOrdinal("summary")),
            Tags = reader.IsDBNull(reader.GetOrdinal("tags")) ? null : reader.GetString(reader.GetOrdinal("tags")),
            Department = reader.IsDBNull(reader.GetOrdinal("department")) ? null : reader.GetString(reader.GetOrdinal("department")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
        };
    }
}
