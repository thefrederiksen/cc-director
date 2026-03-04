namespace CcDirector.DocumentLibrary.Models;

/// <summary>
/// A registered document library folder. Matches cc-vault JSON output.
/// </summary>
public class Library
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Owner { get; set; }
    public int Recursive { get; set; } = 1;
    public int Enabled { get; set; } = 1;
    public string? LastScanned { get; set; }
    public string? CreatedAt { get; set; }

    // Stats (populated by library show --json)
    public CatalogStats? Stats { get; set; }
}

public class CatalogStats
{
    public int Total { get; set; }
    public int Summarized { get; set; }
    public int Pending { get; set; }
    public int Errors { get; set; }
    public int Skipped { get; set; }
    public int Missing { get; set; }
}
