using System.Text.Json.Serialization;

namespace CcDirector.DocumentLibrary.Models;

/// <summary>
/// A cataloged file entry. Matches cc-vault JSON output.
/// </summary>
public class CatalogEntry
{
    public int Id { get; set; }

    [JsonPropertyName("library_id")]
    public int LibraryId { get; set; }

    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("file_ext")]
    public string FileExt { get; set; } = string.Empty;

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("file_hash")]
    public string? FileHash { get; set; }

    [JsonPropertyName("file_modified_at")]
    public string? FileModifiedAt { get; set; }

    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? Tags { get; set; }
    public string? Department { get; set; }
    public string Status { get; set; } = "pending";

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    /// <summary>Human-readable file size (e.g. "2.1 MB").</summary>
    public string FileSizeDisplay
    {
        get
        {
            if (FileSize < 1024) return $"{FileSize} B";
            if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
            return $"{FileSize / (1024.0 * 1024.0):F1} MB";
        }
    }

    /// <summary>Human-readable modified date.</summary>
    public string ModifiedDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(FileModifiedAt)) return "-";
            if (DateTime.TryParse(FileModifiedAt, out var dt))
                return dt.ToString("yyyy-MM-dd HH:mm");
            return FileModifiedAt;
        }
    }

    /// <summary>Short status label for display.</summary>
    public string StatusDisplay => Status switch
    {
        "summarized" => "OK",
        "pending" => "Pending",
        "error" => "Error",
        "skipped" => "Skip",
        "missing" => "Missing",
        _ => Status,
    };
}
