using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunicationManager.Models;
using CommunicationManager.Services;
using Microsoft.Data.Sqlite;

namespace CommunicationManager.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ContentService _contentService;
    private readonly DispatcherTimer _pollTimer;
    private bool _isRefreshing;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<ContentItem> _pendingItems = new();

    [ObservableProperty]
    private ObservableCollection<ContentItem> _approvedItems = new();

    [ObservableProperty]
    private ObservableCollection<ContentItem> _rejectedItems = new();

    [ObservableProperty]
    private ObservableCollection<ContentItem> _sentItems = new();

    [ObservableProperty]
    private ContentItem? _selectedItem;

    [ObservableProperty]
    private string _selectedTab = "Pending";

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editContent = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _approvedCount;

    [ObservableProperty]
    private int _rejectedCount;

    [ObservableProperty]
    private int _sentCount;

    [ObservableProperty]
    private bool _isPreviewMode = true;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private string _selectedPlatformFilter = "All";

    [ObservableProperty]
    private string _selectedDateFilter = "All Upcoming";

    [ObservableProperty]
    private string _selectedApprovedView = "List";

    [ObservableProperty]
    private ObservableCollection<ContentItem> _filteredItems = new();

    [RelayCommand]
    private void TogglePreviewMode()
    {
        IsPreviewMode = !IsPreviewMode;
        StatusMessage = IsPreviewMode ? "Preview mode" : "Raw mode";
    }

    [RelayCommand]
    private void SetPlatformFilter(string platform)
    {
        FileLog.Write($"[CommunicationManager.VM] SetPlatformFilter: {platform}");
        SelectedPlatformFilter = platform;
        RebuildFilteredItems();
    }

    [RelayCommand]
    private void SetDateFilter(string dateFilter)
    {
        FileLog.Write($"[CommunicationManager.VM] SetDateFilter: {dateFilter}");
        SelectedDateFilter = dateFilter;
        RebuildFilteredItems();
    }

    [RelayCommand]
    private void SetApprovedView(string view)
    {
        FileLog.Write($"[CommunicationManager.VM] SetApprovedView: {view}");
        SelectedApprovedView = view;
    }

    public MainViewModel()
    {
        FileLog.Write("[CommunicationManager.VM] Constructor");
        // Get content path - look for content folder relative to exe or use default
        var contentPath = GetContentPath();

        _contentService = new ContentService(contentPath);

        // Set up polling timer (every 5 seconds)
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _pollTimer.Tick += async (s, e) => await RefreshAsync();
    }

    private static string GetContentPath()
    {
        var path = CcStorage.ToolConfig("comm-queue");
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task InitializeAsync()
    {
        FileLog.Write("[CommunicationManager.VM] InitializeAsync");
        await _contentService.InitializeAsync();
        await RefreshAsync();
        // Start polling after initial load
        _pollTimer.Start();
    }

    /// <summary>
    /// Start the polling timer. Call when the panel becomes visible.
    /// </summary>
    public void StartPolling()
    {
        FileLog.Write("[CommunicationManager.VM] StartPolling");
        _pollTimer.Start();
    }

    /// <summary>
    /// Stop the polling timer. Call when the panel is hidden to save resources.
    /// </summary>
    public void StopPolling()
    {
        FileLog.Write("[CommunicationManager.VM] StopPolling");
        _pollTimer.Stop();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            var pending = await _contentService.LoadPendingItemsAsync();
            var approved = await _contentService.LoadApprovedItemsAsync();
            var rejected = await _contentService.LoadRejectedItemsAsync();
            var sent = await _contentService.LoadPostedItemsAsync();

            // Track if we had no items before
            var hadNoItems = PendingItems.Count == 0 && ApprovedItems.Count == 0
                          && RejectedItems.Count == 0 && SentItems.Count == 0;

            // Update existing collections to preserve bindings
            UpdateCollection(PendingItems, pending);
            UpdateCollection(ApprovedItems, approved);
            UpdateCollection(RejectedItems, rejected);
            UpdateCollection(SentItems, sent);

            PendingCount = pending.Count;
            ApprovedCount = approved.Count;
            RejectedCount = rejected.Count;
            SentCount = sent.Count;

            RebuildFilteredItems();

            // Auto-select first item if nothing selected
            if (SelectedItem == null)
            {
                AutoSelectFirstItem();
            }
            // Also auto-select if we just got items for the first time
            else if (hadNoItems && pending.Count > 0)
            {
                AutoSelectFirstItem();
            }

            StatusMessage = $"Loaded {pending.Count} pending, {approved.Count} approved, {rejected.Count} rejected, {sent.Count} sent";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationManager.VM] RefreshAsync FAILED: {ex.Message}");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void AutoSelectFirstItem()
    {
        if (FilteredItems.Count > 0)
        {
            SelectedItem = FilteredItems[0];
        }
    }

    public void OnTabChanged(string tabName)
    {
        SelectedTab = tabName;
        SelectedPlatformFilter = "All";
        SelectedDateFilter = "All Upcoming";
        RebuildFilteredItems();
        AutoSelectFirstItem();
    }

    private void RebuildFilteredItems()
    {
        var source = SelectedTab switch
        {
            "Pending" => PendingItems,
            "Approved" => ApprovedItems,
            "Rejected" => RejectedItems,
            "Sent" => SentItems,
            _ => PendingItems
        };

        FilteredItems.Clear();

        foreach (var item in source)
        {
            if (SelectedPlatformFilter != "All" &&
                !item.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SelectedTab == "Approved" && !PassesDateFilter(item))
            {
                continue;
            }

            FilteredItems.Add(item);
        }
    }

    private bool PassesDateFilter(ContentItem item)
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var endOfWeek = today.AddDays(7 - (int)today.DayOfWeek);

        // For ASAP items, treat them as "today"
        var effectiveDate = item.IsScheduled ? item.ScheduledFor.GetValueOrDefault().Date : today;

        return SelectedDateFilter switch
        {
            "Today" => effectiveDate == today,
            "Tomorrow" => effectiveDate == tomorrow,
            "This Week" => effectiveDate >= today && effectiveDate <= endOfWeek,
            "All Upcoming" => effectiveDate >= today || item.IsAsap || item.IsHold,
            _ => true
        };
    }

    public int GetDateFilterCount(string filter)
    {
        var source = ApprovedItems;
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var endOfWeek = today.AddDays(7 - (int)today.DayOfWeek);

        return filter switch
        {
            "Today" => source.Count(i => (SelectedPlatformFilter == "All" || i.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                && ((i.IsScheduled && i.ScheduledFor.GetValueOrDefault().Date == today) || i.IsAsap)),
            "Tomorrow" => source.Count(i => (SelectedPlatformFilter == "All" || i.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                && i.IsScheduled && i.ScheduledFor.GetValueOrDefault().Date == tomorrow),
            "This Week" => source.Count(i => (SelectedPlatformFilter == "All" || i.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                && ((i.IsScheduled && i.ScheduledFor.GetValueOrDefault().Date >= today && i.ScheduledFor.GetValueOrDefault().Date <= endOfWeek) || i.IsAsap)),
            "All Upcoming" => source.Count(i => (SelectedPlatformFilter == "All" || i.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                && ((i.IsScheduled && i.ScheduledFor.GetValueOrDefault().Date >= today) || i.IsAsap || i.IsHold)),
            _ => source.Count
        };
    }

    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Approving...";

        if (await _contentService.ApproveItemAsync(item))
        {
            StatusMessage = $"Approved: {item.DisplayTitle}";
            await RefreshAsync();
            SelectNextItem();
        }
        else
        {
            StatusMessage = "Failed to approve item";
        }
    }

    [RelayCommand]
    private async Task ApproveWithScheduleAsync((string Timing, DateTime? ScheduledFor) schedule)
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Approving with schedule...";

        if (await _contentService.ApproveWithScheduleAsync(item, schedule.Timing, schedule.ScheduledFor))
        {
            var desc = schedule.Timing == "hold" ? "on hold"
                : schedule.ScheduledFor.HasValue ? $"scheduled for {schedule.ScheduledFor:MMM d, h:mm tt}"
                : "ASAP";
            StatusMessage = $"Approved ({desc}): {item.DisplayTitle}";
            await RefreshAsync();
            SelectNextItem();
        }
        else
        {
            StatusMessage = "Failed to approve item";
        }
    }

    [RelayCommand]
    private async Task RescheduleAsync((string Timing, DateTime? ScheduledFor) schedule)
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Rescheduling...";

        if (await _contentService.UpdateScheduleAsync(item, schedule.Timing, schedule.ScheduledFor))
        {
            var desc = schedule.Timing == "hold" ? "on hold"
                : schedule.ScheduledFor.HasValue ? $"scheduled for {schedule.ScheduledFor:MMM d, h:mm tt}"
                : "ASAP";
            StatusMessage = $"Rescheduled ({desc}): {item.DisplayTitle}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = "Failed to reschedule item";
        }
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Rejecting...";

        if (await _contentService.RejectItemAsync(item))
        {
            StatusMessage = $"Rejected: {item.DisplayTitle}";
            await RefreshAsync();
            SelectNextItem();
        }
        else
        {
            StatusMessage = "Failed to reject item";
        }
    }

    [RelayCommand]
    private async Task RejectWithReasonAsync(string reason)
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Rejecting...";

        if (await _contentService.RejectItemAsync(item, reason))
        {
            StatusMessage = $"Rejected: {item.DisplayTitle}";
            await RefreshAsync();
            SelectNextItem();
        }
        else
        {
            StatusMessage = "Failed to reject item";
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedItem == null) return;

        var result = MessageBox.Show(
            Application.Current.MainWindow,
            $"Are you sure you want to permanently delete this item?\n\n{SelectedItem.DisplayTitle}",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var item = SelectedItem;
        StatusMessage = "Deleting...";

        if (await _contentService.DeleteItemAsync(item))
        {
            StatusMessage = $"Deleted: {item.DisplayTitle}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = "Failed to delete item";
        }
    }

    [RelayCommand]
    private void StartEdit()
    {
        if (SelectedItem == null) return;
        EditContent = SelectedItem.Content;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditContent = string.Empty;
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (SelectedItem == null) return;

        SelectedItem.Content = EditContent;

        if (await _contentService.SaveItemAsync(SelectedItem))
        {
            StatusMessage = "Changes saved";
            IsEditing = false;
            OnPropertyChanged(nameof(SelectedItem));
        }
        else
        {
            StatusMessage = "Failed to save changes";
        }
    }

    [RelayCommand]
    private void OpenContextUrl()
    {
        if (SelectedItem?.ContextUrl == null) return;
        OpenUrl(SelectedItem.ContextUrl);
    }

    [RelayCommand]
    private void OpenDestinationUrl()
    {
        if (SelectedItem?.DestinationUrl == null) return;
        OpenUrl(SelectedItem.DestinationUrl);
    }

    [RelayCommand]
    private void CopyDestinationUrl()
    {
        if (SelectedItem?.DestinationUrl == null) return;
        Clipboard.SetText(SelectedItem.DestinationUrl);
        StatusMessage = "Destination URL copied to clipboard";
    }

    [RelayCommand]
    private void OpenRecipientUrl()
    {
        if (SelectedItem?.Recipient?.ProfileUrl == null) return;
        OpenUrl(SelectedItem.Recipient.ProfileUrl);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, $"Failed to open URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Skip()
    {
        SelectNextItem();
    }

    [RelayCommand]
    private async Task MoveToReviewAsync()
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Moving to review...";

        if (await _contentService.MoveToReviewAsync(item))
        {
            StatusMessage = $"Moved to review: {item.DisplayTitle}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = "Failed to move item";
        }
    }

    [RelayCommand]
    private async Task PostToLinkedInAsync()
    {
        if (SelectedItem == null) return;
        if (!SelectedItem.IsLinkedIn)
        {
            StatusMessage = "This item is not a LinkedIn post";
            return;
        }

        var item = SelectedItem;
        StatusMessage = "Posting to LinkedIn...";

        try
        {
            // Get first image temp path if available
            string? imagePath = null;
            if (item.Media != null && item.Media.Count > 0)
            {
                var firstImage = item.Media.FirstOrDefault(m => m.IsImage);
                if (firstImage != null && firstImage.HasTempFile)
                {
                    imagePath = firstImage.TempPath;
                }
                else if (firstImage != null)
                {
                    // Extract to temp if not already done
                    imagePath = _contentService.ExtractMediaToTemp(firstImage.Id);
                }
            }

            // Build command arguments
            var args = new List<string> { "create" };

            // Escape content for command line
            var escapedContent = item.Content.Replace("\"", "\\\"");
            args.Add($"\"{escapedContent}\"");

            if (!string.IsNullOrEmpty(imagePath))
            {
                args.Add("--image");
                args.Add($"\"{imagePath}\"");
            }

            // Run cc_linkedin create command
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(CcStorage.Bin(), "cc-linkedin.exe"),
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                StatusMessage = "Failed to start cc_linkedin";
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                // Mark as posted
                if (await _contentService.MarkAsPostedAsync(item))
                {
                    StatusMessage = $"Posted to LinkedIn: {item.DisplayTitle}";
                    await RefreshAsync();
                }
                else
                {
                    StatusMessage = "Posted but failed to update status";
                }
            }
            else
            {
                StatusMessage = $"LinkedIn posting failed: {error}";
                MessageBox.Show(Application.Current.MainWindow, $"LinkedIn posting failed:\n\n{error}\n\n{output}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show(Application.Current.MainWindow, $"Failed to post to LinkedIn: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private enum DispatchResult { Sent, Failed, Skipped }

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> _gmailAccounts = LoadGmailAccounts();

    private static HashSet<string> LoadGmailAccounts()
    {
        var configPath = CcStorage.ConfigJson();
        if (!File.Exists(configPath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("comm_manager", out var cm) ||
            !cm.TryGetProperty("send_from_accounts", out var accounts))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var acct in accounts.EnumerateObject())
        {
            if (acct.Value.TryGetProperty("tool", out var tool))
            {
                var toolStr = tool.GetString() ?? "";
                if (toolStr.Contains("gmail", StringComparison.OrdinalIgnoreCase))
                    result.Add(acct.Name);
            }
        }

        FileLog.Write($"[CommunicationManager.VM] LoadGmailAccounts: {string.Join(", ", result)}");
        return result;
    }

    [RelayCommand]
    private async Task SendAllAsync()
    {
        FileLog.Write("[CommunicationManager.VM] SendAllAsync: starting manual dispatch");
        IsSending = true;
        StatusMessage = "Sending approved items...";

        var sent = 0;
        var failed = 0;
        var skipped = 0;

        try
        {
            var dbPath = CcStorage.CommQueueDb();
            if (!File.Exists(dbPath))
            {
                StatusMessage = "No communications database found";
                return;
            }

            var items = await GetApprovedItemsAsync(dbPath);
            FileLog.Write($"[CommunicationManager.VM] SendAllAsync: found {items.Count} approved items");

            if (items.Count == 0)
            {
                StatusMessage = "No approved items to send";
                return;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                StatusMessage = $"Dispatching {i + 1}/{items.Count}: [{item.Platform}] ticket #{item.TicketNumber}";

                var result = await DispatchItemAsync(item, dbPath);
                switch (result)
                {
                    case DispatchResult.Sent: sent++; break;
                    case DispatchResult.Failed: failed++; break;
                    case DispatchResult.Skipped: skipped++; break;
                }
            }

            var resultMsg = $"Dispatch complete: {sent} sent";
            if (failed > 0)
                resultMsg += $", {failed} failed";
            if (skipped > 0)
                resultMsg += $", {skipped} skipped";

            StatusMessage = resultMsg;
            FileLog.Write($"[CommunicationManager.VM] SendAllAsync: {resultMsg}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationManager.VM] SendAllAsync FAILED: {ex.Message}");
            StatusMessage = $"Send error: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    private static async Task<List<QueuedItem>> GetApprovedItemsAsync(string dbPath)
    {
        FileLog.Write($"[CommunicationManager.VM] GetApprovedItemsAsync: reading from {dbPath}");
        var items = new List<QueuedItem>();
        var connectionString = $"Data Source={dbPath};Mode=ReadOnly";

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ticket_number, platform, type, content, persona, send_from,
                   email_specific, linkedin_specific, reddit_specific,
                   destination_url, context_url
            FROM communications
            WHERE status = 'approved'
            AND (send_timing IS NULL OR send_timing != 'hold')
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new QueuedItem
            {
                Id = reader.GetString(0),
                TicketNumber = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                Platform = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Type = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Content = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Persona = reader.IsDBNull(5) ? "personal" : reader.GetString(5),
                SendFrom = reader.IsDBNull(6) ? null : reader.GetString(6),
                EmailSpecificJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                LinkedInSpecificJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                RedditSpecificJson = reader.IsDBNull(9) ? null : reader.GetString(9),
                DestinationUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
                ContextUrl = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }

        FileLog.Write($"[CommunicationManager.VM] GetApprovedItemsAsync: found {items.Count} items");
        return items;
    }

    private static async Task<DispatchResult> DispatchItemAsync(QueuedItem item, string dbPath)
    {
        FileLog.Write($"[CommunicationManager.VM] DispatchItemAsync: ticket #{item.TicketNumber}, platform={item.Platform}");

        switch (item.Platform.ToLowerInvariant())
        {
            case "email":
                var success = await DispatchEmailItemAsync(item, dbPath);
                return success ? DispatchResult.Sent : DispatchResult.Failed;

            default:
                FileLog.Write($"[CommunicationManager.VM] DispatchItemAsync: platform '{item.Platform}' not yet supported, skipping ticket #{item.TicketNumber}");
                return DispatchResult.Skipped;
        }
    }

    private static async Task<bool> DispatchEmailItemAsync(QueuedItem item, string dbPath)
    {
        FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: ticket #{item.TicketNumber}");

        if (string.IsNullOrEmpty(item.EmailSpecificJson))
        {
            FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: no email_specific data for ticket #{item.TicketNumber}");
            return false;
        }

        EmailSpecificDto? spec;
        try
        {
            spec = JsonSerializer.Deserialize<EmailSpecificDto>(item.EmailSpecificJson, _jsonOptions);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: failed to parse email_specific for ticket #{item.TicketNumber}: {ex.Message}");
            return false;
        }

        if (spec?.To == null || spec.To.Count == 0)
        {
            FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: no recipients for ticket #{item.TicketNumber}");
            return false;
        }

        var sendFrom = item.SendFrom ?? item.Persona;
        var useGmail = _gmailAccounts.Contains(sendFrom) ||
                       sendFrom.Contains("@gmail.com", StringComparison.OrdinalIgnoreCase);
        var toolName = useGmail ? "cc-gmail" : "cc-outlook";
        var toolPath = Path.Combine(CcStorage.Bin(), useGmail ? "cc-gmail.exe" : "cc-outlook.exe");

        var to = string.Join(",", spec.To);
        var subject = spec.Subject ?? "(no subject)";

        FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: ticket #{item.TicketNumber} to {to} via {toolName}");

        var args = new List<string> { "send", "-t", to, "-s", subject, "-b", item.Content, "--html" };

        if (spec.Cc != null && spec.Cc.Count > 0)
        {
            args.Add("--cc");
            args.Add(string.Join(",", spec.Cc));
        }
        if (spec.Bcc != null && spec.Bcc.Count > 0)
        {
            args.Add("--bcc");
            args.Add(string.Join(",", spec.Bcc));
        }

        var attachFlag = useGmail ? "--attach" : "-a";
        if (spec.Attachments != null)
        {
            foreach (var attachment in spec.Attachments)
            {
                if (File.Exists(attachment))
                {
                    args.Add(attachFlag);
                    args.Add(attachment);
                }
            }
        }

        return await RunToolAndMarkPostedAsync(toolPath, args, item, dbPath, toolName);
    }

    private static async Task<bool> RunToolAndMarkPostedAsync(
        string toolPath, List<string> args, QueuedItem item, string dbPath, string toolName)
    {
        FileLog.Write($"[CommunicationManager.VM] RunToolAndMarkPostedAsync: ticket #{item.TicketNumber} via {toolName}");

        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null)
        {
            FileLog.Write($"[CommunicationManager.VM] RunToolAndMarkPostedAsync: failed to start {toolPath}");
            return false;
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await stderrTask;
        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            MarkPosted(item.Id, dbPath);
            FileLog.Write($"[CommunicationManager.VM] RunToolAndMarkPostedAsync: ticket #{item.TicketNumber} sent OK via {toolName}");
            return true;
        }

        var error = string.IsNullOrEmpty(stderr) ? stdout : stderr;
        FileLog.Write($"[CommunicationManager.VM] RunToolAndMarkPostedAsync: ticket #{item.TicketNumber} FAILED via {toolName}: {error}");
        return false;
    }

    private static void MarkPosted(string id, string dbPath)
    {
        FileLog.Write($"[CommunicationManager.VM] MarkPosted: id={id}");
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE communications
            SET status = 'posted', posted_at = @now, posted_by = 'cc-director-manual'
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private class QueuedItem
    {
        public string Id { get; set; } = "";
        public int TicketNumber { get; set; }
        public string Platform { get; set; } = "";
        public string Type { get; set; } = "";
        public string Content { get; set; } = "";
        public string Persona { get; set; } = "personal";
        public string? SendFrom { get; set; }
        public string? EmailSpecificJson { get; set; }
        public string? LinkedInSpecificJson { get; set; }
        public string? RedditSpecificJson { get; set; }
        public string? DestinationUrl { get; set; }
        public string? ContextUrl { get; set; }
    }

    private class EmailSpecificDto
    {
        public List<string>? To { get; set; }
        public List<string>? Cc { get; set; }
        public List<string>? Bcc { get; set; }
        public string? Subject { get; set; }
        public List<string>? Attachments { get; set; }
    }

    private void SelectNextItem()
    {
        if (FilteredItems.Count == 0)
        {
            SelectedItem = null;
            return;
        }

        var currentIndex = FilteredItems.IndexOf(SelectedItem ?? FilteredItems[0]);
        if (currentIndex < FilteredItems.Count - 1)
        {
            SelectedItem = FilteredItems[currentIndex + 1];
        }
        else if (FilteredItems.Count > 0)
        {
            SelectedItem = FilteredItems[0];
        }
    }

    private static void UpdateCollection(ObservableCollection<ContentItem> collection, List<ContentItem> newItems)
    {
        collection.Clear();
        foreach (var item in newItems)
        {
            collection.Add(item);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _contentService.Dispose();
    }
}
