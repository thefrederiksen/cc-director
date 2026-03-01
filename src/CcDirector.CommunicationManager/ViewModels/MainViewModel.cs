using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunicationManager.Models;
using CommunicationManager.Services;

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

    [RelayCommand]
    private void TogglePreviewMode()
    {
        IsPreviewMode = !IsPreviewMode;
        StatusMessage = IsPreviewMode ? "Preview mode" : "Raw mode";
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
        var items = SelectedTab switch
        {
            "Pending" => PendingItems,
            "Approved" => ApprovedItems,
            "Rejected" => RejectedItems,
            "Sent" => SentItems,
            _ => PendingItems
        };

        if (items.Count > 0)
        {
            SelectedItem = items[0];
        }
    }

    public void OnTabChanged(string tabName)
    {
        SelectedTab = tabName;
        AutoSelectFirstItem();
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

    private void SelectNextItem()
    {
        var items = SelectedTab switch
        {
            "Pending" => PendingItems,
            "Approved" => ApprovedItems,
            "Rejected" => RejectedItems,
            "Sent" => SentItems,
            _ => PendingItems
        };

        if (items.Count == 0)
        {
            SelectedItem = null;
            return;
        }

        // SelectedItem may be null if nothing is selected; IndexOf returns -1 in that case
        var currentIndex = items.IndexOf(SelectedItem ?? items[0]);
        if (currentIndex < items.Count - 1)
        {
            SelectedItem = items[currentIndex + 1];
        }
        else if (items.Count > 0)
        {
            SelectedItem = items[0];
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
