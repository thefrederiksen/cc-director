using System.Diagnostics;
using CommunicationManager.Models;

namespace CommunicationManager.Services;

public class ContentService : IDisposable
{
    private readonly DatabaseService _db;
    private bool _disposed;

    public string ContentPath => _db.ContentPath;

    public ContentService(string contentBasePath)
    {
        _db = new DatabaseService(contentBasePath);
    }

    /// <summary>
    /// Performs async initialization (directory creation, database schema).
    /// Must be called after construction before any other operations.
    /// </summary>
    public async Task InitializeAsync()
    {
        Debug.WriteLine("[ContentService] InitializeAsync");
        await _db.InitializeAsync();
    }

    public async Task<List<ContentItem>> LoadPendingItemsAsync()
    {
        return await _db.LoadItemsByStatusAsync("pending_review");
    }

    public async Task<List<ContentItem>> LoadApprovedItemsAsync()
    {
        return await _db.LoadItemsByStatusAsync("approved");
    }

    public async Task<List<ContentItem>> LoadRejectedItemsAsync()
    {
        return await _db.LoadItemsByStatusAsync("rejected");
    }

    public async Task<List<ContentItem>> LoadPostedItemsAsync()
    {
        return await _db.LoadItemsByStatusAsync("posted");
    }

    public async Task<bool> ApproveItemAsync(ContentItem item)
    {
        Debug.WriteLine($"[ContentService] ApproveItemAsync: ticket={item.TicketNumber}");
        if (!item.TicketNumber.HasValue) return false;
        return await _db.UpdateStatusAsync(item.TicketNumber.Value, "approved");
    }

    public async Task<bool> RejectItemAsync(ContentItem item, string? reason = null)
    {
        Debug.WriteLine($"[ContentService] RejectItemAsync: ticket={item.TicketNumber}, reason={reason}");
        if (!item.TicketNumber.HasValue) return false;

        var additionalFields = new Dictionary<string, object?>
        {
            { "rejection_reason", reason },
            { "rejected_at", DateTime.UtcNow.ToString("o") },
            { "rejected_by", "user" }
        };

        return await _db.UpdateStatusAsync(item.TicketNumber.Value, "rejected", additionalFields);
    }

    public async Task<bool> DeleteItemAsync(ContentItem item)
    {
        Debug.WriteLine($"[ContentService] DeleteItemAsync: ticket={item.TicketNumber}");
        if (!item.TicketNumber.HasValue) return false;
        return await _db.DeleteAsync(item.TicketNumber.Value);
    }

    public async Task<bool> SaveItemAsync(ContentItem item)
    {
        Debug.WriteLine($"[ContentService] SaveItemAsync: ticket={item.TicketNumber}");
        if (!item.TicketNumber.HasValue) return false;
        return await _db.UpdateContentAsync(item.TicketNumber.Value, item.Content);
    }

    public async Task<bool> MoveToReviewAsync(ContentItem item)
    {
        Debug.WriteLine($"[ContentService] MoveToReviewAsync: ticket={item.TicketNumber}");
        if (!item.TicketNumber.HasValue) return false;

        var additionalFields = new Dictionary<string, object?>
        {
            { "rejection_reason", null },
            { "rejected_at", null },
            { "rejected_by", null }
        };

        return await _db.UpdateStatusAsync(item.TicketNumber.Value, "pending_review", additionalFields);
    }

    public async Task<bool> MarkAsPostedAsync(ContentItem item, string? postedUrl = null)
    {
        Debug.WriteLine($"[ContentService] MarkAsPostedAsync: ticket={item.TicketNumber}, url={postedUrl}");
        if (!item.TicketNumber.HasValue) return false;

        var additionalFields = new Dictionary<string, object?>
        {
            { "posted_at", DateTime.UtcNow.ToString("o") },
            { "posted_by", "user" },
            { "posted_url", postedUrl }
        };

        return await _db.UpdateStatusAsync(item.TicketNumber.Value, "posted", additionalFields);
    }

    public async Task<Dictionary<string, int>> GetStatsAsync()
    {
        return await _db.GetStatsAsync();
    }

    /// <summary>
    /// Retrieves media BLOB data by ID.
    /// </summary>
    public async Task<byte[]?> GetMediaDataAsync(int mediaId)
    {
        return await _db.GetMediaDataAsync(mediaId);
    }

    /// <summary>
    /// Extracts media BLOB to a temp file for UI display.
    /// Returns the temp file path.
    /// </summary>
    public async Task<string?> ExtractMediaToTempAsync(int mediaId)
    {
        return await _db.ExtractMediaToTempAsync(mediaId);
    }

    /// <summary>
    /// Extracts media BLOB to a temp file synchronously for UI display.
    /// Returns the temp file path.
    /// </summary>
    public string? ExtractMediaToTemp(int mediaId)
    {
        return _db.ExtractMediaToTemp(mediaId);
    }

    /// <summary>
    /// Cleans up temp media files older than specified age.
    /// </summary>
    public void CleanupTempMedia(TimeSpan maxAge)
    {
        _db.CleanupTempMedia(maxAge);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _db.Dispose();
    }
}
