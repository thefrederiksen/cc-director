using CcDirector.Core.Communications.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Communications.Services;

public class ContentService : IDisposable
{
    private readonly DatabaseService _db;
    private bool _disposed;

    public string ContentPath => _db.ContentPath;

    public ContentService(string contentBasePath)
    {
        _db = new DatabaseService(contentBasePath);
    }

    public async Task InitializeAsync()
    {
        FileLog.Write("[ContentService] InitializeAsync");
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
        FileLog.Write($"[ContentService] ApproveItemAsync: ticket={item.TicketNumber}");
        if (!item.TicketNumber.HasValue) return false;
        return await _db.UpdateStatusAsync(item.TicketNumber.Value, "approved");
    }

    public async Task<bool> ApproveWithScheduleAsync(ContentItem item, string sendTiming, DateTime? scheduledFor = null)
    {
        FileLog.Write($"[ContentService] ApproveWithScheduleAsync: ticket={item.TicketNumber}, timing={sendTiming}, scheduledFor={scheduledFor}");
        if (!item.TicketNumber.HasValue) return false;

        var additionalFields = new Dictionary<string, object?>
        {
            { "send_timing", sendTiming },
            { "scheduled_for", scheduledFor?.ToString("o") }
        };

        return await _db.UpdateStatusAsync(item.TicketNumber.Value, "approved", additionalFields);
    }

    public async Task<bool> UpdateScheduleAsync(ContentItem item, string sendTiming, DateTime? scheduledFor = null)
    {
        FileLog.Write($"[ContentService] UpdateScheduleAsync: ticket={item.TicketNumber}, timing={sendTiming}, scheduledFor={scheduledFor}");
        if (!item.TicketNumber.HasValue) return false;

        var additionalFields = new Dictionary<string, object?>
        {
            { "send_timing", sendTiming },
            { "scheduled_for", scheduledFor?.ToString("o") }
        };

        return await _db.UpdateStatusAsync(item.TicketNumber.Value, item.Status, additionalFields);
    }

    public async Task<bool> RejectItemAsync(ContentItem item, string? reason = null)
    {
        FileLog.Write($"[ContentService] RejectItemAsync: ticket={item.TicketNumber}, reason={reason}");
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
        FileLog.Write($"[ContentService] DeleteItemAsync: ticket={item.TicketNumber}");
        if (!item.TicketNumber.HasValue) return false;
        return await _db.DeleteAsync(item.TicketNumber.Value);
    }

    public async Task<bool> SaveItemAsync(ContentItem item)
    {
        FileLog.Write($"[ContentService] SaveItemAsync: ticket={item.TicketNumber}");
        if (!item.TicketNumber.HasValue) return false;
        return await _db.UpdateContentAsync(item.TicketNumber.Value, item.Content);
    }

    public async Task<bool> MoveToReviewAsync(ContentItem item)
    {
        FileLog.Write($"[ContentService] MoveToReviewAsync: ticket={item.TicketNumber}");
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
        FileLog.Write($"[ContentService] MarkAsPostedAsync: ticket={item.TicketNumber}, url={postedUrl}");
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

    public async Task<byte[]?> GetMediaDataAsync(int mediaId)
    {
        return await _db.GetMediaDataAsync(mediaId);
    }

    public async Task<string?> ExtractMediaToTempAsync(int mediaId)
    {
        return await _db.ExtractMediaToTempAsync(mediaId);
    }

    public string? ExtractMediaToTemp(int mediaId)
    {
        return _db.ExtractMediaToTemp(mediaId);
    }

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
