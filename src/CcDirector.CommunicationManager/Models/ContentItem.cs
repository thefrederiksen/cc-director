using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;

namespace CommunicationManager.Models;

public class ContentItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ticket_number")]
    public int? TicketNumber { get; set; }

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("persona")]
    public string Persona { get; set; } = string.Empty;

    [JsonPropertyName("persona_display")]
    public string PersonaDisplay { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("created_by")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending_review";

    [JsonPropertyName("context_url")]
    public string? ContextUrl { get; set; }

    [JsonPropertyName("context_title")]
    public string? ContextTitle { get; set; }

    [JsonPropertyName("context_author")]
    public string? ContextAuthor { get; set; }

    [JsonPropertyName("destination_url")]
    public string? DestinationUrl { get; set; }

    [JsonPropertyName("campaign_id")]
    public string? CampaignId { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("media")]
    public List<MediaItem>? Media { get; set; }

    [JsonPropertyName("recipient")]
    public RecipientInfo? Recipient { get; set; }

    [JsonPropertyName("linkedin_specific")]
    public LinkedInSpecific? LinkedInSpecific { get; set; }

    [JsonPropertyName("twitter_specific")]
    public TwitterSpecific? TwitterSpecific { get; set; }

    [JsonPropertyName("reddit_specific")]
    public RedditSpecific? RedditSpecific { get; set; }

    [JsonPropertyName("email_specific")]
    public EmailSpecific? EmailSpecific { get; set; }

    [JsonPropertyName("article_specific")]
    public ArticleSpecific? ArticleSpecific { get; set; }

    [JsonPropertyName("thread_content")]
    public List<string>? ThreadContent { get; set; }

    [JsonPropertyName("rejection_reason")]
    public string? RejectionReason { get; set; }

    [JsonPropertyName("rejected_at")]
    public DateTime? RejectedAt { get; set; }

    [JsonPropertyName("rejected_by")]
    public string? RejectedBy { get; set; }

    [JsonPropertyName("posted_at")]
    public DateTime? PostedAt { get; set; }

    [JsonPropertyName("posted_by")]
    public string? PostedBy { get; set; }

    [JsonPropertyName("posted_url")]
    public string? PostedUrl { get; set; }

    [JsonPropertyName("post_id")]
    public string? PostId { get; set; }

    // Dispatch fields
    [JsonPropertyName("send_timing")]
    public string SendTiming { get; set; } = "asap";

    [JsonPropertyName("scheduled_for")]
    public DateTime? ScheduledFor { get; set; }

    [JsonPropertyName("send_from")]
    public string? SendFrom { get; set; }

    // File path for tracking which file this came from
    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    // Display helpers
    [JsonIgnore]
    public string DisplayTitle => GetDisplayTitle();

    [JsonIgnore]
    public string TicketDisplay => TicketNumber.HasValue ? $"#{TicketNumber}" : "#?";

    [JsonIgnore]
    public string PlatformIcon => GetPlatformIcon();

    [JsonIgnore]
    public string TypeIcon => GetTypeIcon();

    [JsonIgnore]
    public string SendFromDisplay => GetSendFromDisplay();

    [JsonIgnore]
    public string SendTimingDisplay => GetSendTimingDisplay();

    [JsonIgnore]
    public string PostedAtDisplay => PostedAt.HasValue ? $"Sent: {PostedAt:MMM d, yyyy 'at' h:mm tt}" : "";

    [JsonIgnore]
    public string RejectedAtDisplay => RejectedAt.HasValue ? $"Rejected: {RejectedAt:MMM d, yyyy 'at' h:mm tt}" : "";

    [JsonIgnore]
    public bool HasMedia => Media != null && Media.Count > 0;

    [JsonIgnore]
    public bool IsLinkedIn => Platform?.Equals("linkedin", StringComparison.OrdinalIgnoreCase) == true;

    [JsonIgnore]
    public bool IsEmail => Platform?.Equals("email", StringComparison.OrdinalIgnoreCase) == true;

    [JsonIgnore]
    public bool HasAttachments => EmailSpecific?.Attachments?.Count > 0;

    [JsonIgnore]
    public List<AttachmentDisplayItem> AttachmentDisplayItems => GetAttachmentDisplayItems();

    [JsonIgnore]
    public int MediaCount => Media?.Count ?? 0;

    [JsonIgnore]
    public string MediaCountDisplay => MediaCount > 0 ? $"{MediaCount} attachment{(MediaCount > 1 ? "s" : "")}" : "";

    private static readonly Dictionary<string, string> SendFromEmails = new()
    {
        { "mindzie", "user@company.com" },
        { "personal", "user@personal.com" },
        { "consulting", "user@consulting.com" }
    };

    private string GetSendFromDisplay()
    {
        // Try SendFrom field first
        if (!string.IsNullOrEmpty(SendFrom))
        {
            return SendFromEmails.TryGetValue(SendFrom.ToLower(), out var email)
                ? email
                : SendFrom;
        }

        // Fall back to Persona for email platform
        if (Platform.Equals("email", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Persona))
        {
            return SendFromEmails.TryGetValue(Persona.ToLower(), out var email)
                ? email
                : "Not specified";
        }

        return "Not specified";
    }

    private string GetSendTimingDisplay()
    {
        return SendTiming?.ToLower() switch
        {
            "scheduled" when ScheduledFor.HasValue => $"Scheduled for {ScheduledFor:MMM d, yyyy 'at' h:mm tt}",
            "hold" => "On hold (manual dispatch)",
            "immediate" or "asap" or _ => "Immediately when approved"
        };
    }

    private string GetDisplayTitle()
    {
        if (!string.IsNullOrEmpty(ContextTitle))
            return $"Re: {ContextTitle}";

        if (ArticleSpecific != null && !string.IsNullOrEmpty(ArticleSpecific.Title))
            return ArticleSpecific.Title;

        if (RedditSpecific != null && !string.IsNullOrEmpty(RedditSpecific.Title))
            return RedditSpecific.Title;

        if (EmailSpecific != null && !string.IsNullOrEmpty(EmailSpecific.Subject))
            return EmailSpecific.Subject;

        // Truncate content for display
        var preview = Content.Length > 50 ? Content[..50] + "..." : Content;
        return preview.Replace("\n", " ").Replace("\r", "");
    }

    private string GetPlatformIcon()
    {
        return Platform.ToLower() switch
        {
            "linkedin" => "LI",
            "twitter" => "X",
            "reddit" => "R",
            "youtube" => "YT",
            "email" => "@",
            "blog" => "B",
            _ => "?"
        };
    }

    private string GetTypeIcon()
    {
        return Type.ToLower() switch
        {
            "post" => "POST",
            "comment" => "CMT",
            "reply" => "RPL",
            "message" => "MSG",
            "article" => "ART",
            "email" => "EMAIL",
            _ => Type.ToUpper()
        };
    }

    private List<AttachmentDisplayItem> GetAttachmentDisplayItems()
    {
        if (EmailSpecific?.Attachments == null || EmailSpecific.Attachments.Count == 0)
            return new List<AttachmentDisplayItem>();

        return EmailSpecific.Attachments.Select(path =>
        {
            var fileName = Path.GetFileName(path);
            string sizeDisplay = "";
            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    sizeDisplay = FormatFileSize(info.Length);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ContentItem] Failed to get file size for {path}: {ex.Message}"); }

            return new AttachmentDisplayItem
            {
                FileName = fileName,
                FilePath = path,
                SizeDisplay = sizeDisplay
            };
        }).ToList();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

public class AttachmentDisplayItem
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string SizeDisplay { get; set; } = string.Empty;
    public string DisplayText => string.IsNullOrEmpty(SizeDisplay) ? FileName : $"{FileName} ({SizeDisplay})";
    public bool FileExists => File.Exists(FilePath);

    public ICommand OpenCommand => new RelayCommand(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    private class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}

public class MediaItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("alt_text")]
    public string? AltText { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    // Temp file path for display (populated by ExtractMediaToTemp)
    [JsonIgnore]
    public string? TempPath { get; set; }

    // Display helpers
    [JsonIgnore]
    public string DisplayName => Filename ?? $"media_{Id}";

    [JsonIgnore]
    public string FileSizeDisplay => FileSize.HasValue ? FormatFileSize(FileSize.Value) : "";

    [JsonIgnore]
    public bool IsImage => MimeType?.StartsWith("image/") == true ||
        Type.Equals("image", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasTempFile => !string.IsNullOrEmpty(TempPath) && File.Exists(TempPath);

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

public class RecipientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("company")]
    public string? Company { get; set; }

    [JsonPropertyName("profile_url")]
    public string? ProfileUrl { get; set; }
}

public class LinkedInSpecific
{
    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    [JsonPropertyName("schedule_time")]
    public DateTime? ScheduleTime { get; set; }
}

public class TwitterSpecific
{
    [JsonPropertyName("is_thread")]
    public bool IsThread { get; set; }

    [JsonPropertyName("thread_position")]
    public int? ThreadPosition { get; set; }

    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; set; }

    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; set; }

    [JsonPropertyName("quote_tweet_url")]
    public string? QuoteTweetUrl { get; set; }
}

public class RedditSpecific
{
    [JsonPropertyName("subreddit")]
    public string? Subreddit { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("flair")]
    public string? Flair { get; set; }

    [JsonPropertyName("subreddit_url")]
    public string? SubredditUrl { get; set; }

    [JsonPropertyName("parent_comment")]
    public string? ParentComment { get; set; }
}

public class EmailSpecific
{
    [JsonPropertyName("to")]
    public List<string>? To { get; set; }

    [JsonPropertyName("cc")]
    public List<string>? Cc { get; set; }

    [JsonPropertyName("bcc")]
    public List<string>? Bcc { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("reply_to_message_id")]
    public string? ReplyToMessageId { get; set; }

    [JsonPropertyName("attachments")]
    public List<string>? Attachments { get; set; }
}

public class ArticleSpecific
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("target_platforms")]
    public List<string>? TargetPlatforms { get; set; }

    [JsonPropertyName("word_count")]
    public int? WordCount { get; set; }

    [JsonPropertyName("reading_time_minutes")]
    public int? ReadingTimeMinutes { get; set; }

    [JsonPropertyName("cover_image")]
    public string? CoverImage { get; set; }

    [JsonPropertyName("seo_keywords")]
    public List<string>? SeoKeywords { get; set; }
}
