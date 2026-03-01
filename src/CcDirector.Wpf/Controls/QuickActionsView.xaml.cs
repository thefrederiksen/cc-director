using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CcDirector.Core.QuickActions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// ChatGPT-style Quick Actions view with thread list and chat bubbles.
/// Each user message triggers a single-turn Claude Code CLI call.
/// </summary>
public partial class QuickActionsView : UserControl
{
    private readonly ObservableCollection<ThreadViewModel> _threads = new();
    private readonly ObservableCollection<ChatBubbleViewModel> _chatBubbles = new();
    private QuickActionDatabase? _db;
    private QuickActionService? _service;
    private string? _activeThreadId;
    private bool _isProcessing;
    private CancellationTokenSource? _processingCts;

    // Cached frozen brushes
    private static readonly SolidColorBrush UserBubbleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x5A, 0x9E)));
    private static readonly SolidColorBrush AssistantBubbleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)));
    private static readonly SolidColorBrush ThinkingBubbleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)));
    private static readonly SolidColorBrush WhiteBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly SolidColorBrush DimTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public QuickActionsView()
    {
        InitializeComponent();
        ThreadList.ItemsSource = _threads;
        ChatMessages.ItemsSource = _chatBubbles;

        Loaded += QuickActionsView_Loaded;
    }

    private void QuickActionsView_Loaded(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[QuickActionsView] Loaded");
        EnsureInitialized();
        _ = LoadThreadsAsync();
    }

    private void EnsureInitialized()
    {
        if (_db != null) return;

        FileLog.Write("[QuickActionsView] Initializing database and service");
        _db = new QuickActionDatabase(CcStorage.QuickActionsDb());
        _service = new QuickActionService(_db);
    }

    private async Task LoadThreadsAsync()
    {
        FileLog.Write("[QuickActionsView] LoadThreadsAsync");
        EnsureInitialized();

        var threads = await Task.Run(() => _db!.GetThreads());
        _threads.Clear();

        foreach (var thread in threads)
        {
            var lastMessage = await Task.Run(() => _db!.GetLastMessage(thread.Id));
            var preview = lastMessage?.Content ?? "";
            if (preview.Length > 80)
                preview = preview[..77] + "...";

            _threads.Add(new ThreadViewModel
            {
                Id = thread.Id,
                Title = thread.Title,
                Preview = preview,
                UpdatedAt = thread.UpdatedAt
            });
        }

        FileLog.Write($"[QuickActionsView] Loaded {_threads.Count} threads");
    }

    private async Task LoadMessagesAsync(string threadId)
    {
        FileLog.Write($"[QuickActionsView] LoadMessagesAsync: threadId={threadId}");
        EnsureInitialized();

        _activeThreadId = threadId;
        _chatBubbles.Clear();

        var messages = await Task.Run(() => _db!.GetMessages(threadId));

        foreach (var msg in messages)
        {
            _chatBubbles.Add(CreateBubble(msg.Role, msg.Content));
        }

        // Show chat area, hide placeholder
        ChatPlaceholder.Visibility = Visibility.Collapsed;
        ChatScrollViewer.Visibility = Visibility.Visible;
        InputBar.Visibility = Visibility.Visible;

        // Auto-scroll to bottom
        _ = Dispatcher.BeginInvoke(() =>
        {
            ChatScrollViewer.ScrollToEnd();
            MessageInput.Focus();
        });

        FileLog.Write($"[QuickActionsView] Loaded {messages.Count} messages");
    }

    // -- Event Handlers --

    private async void BtnNewThread_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[QuickActionsView] BtnNewThread_Click");
        EnsureInitialized();

        var thread = await Task.Run(() => _db!.CreateThread("New Thread"));

        var vm = new ThreadViewModel
        {
            Id = thread.Id,
            Title = thread.Title,
            Preview = "",
            UpdatedAt = thread.UpdatedAt
        };
        _threads.Insert(0, vm);

        // Select the new thread
        ThreadList.SelectedItem = vm;
    }

    private async void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThreadList.SelectedItem is not ThreadViewModel vm) return;

        FileLog.Write($"[QuickActionsView] ThreadList_SelectionChanged: id={vm.Id}");
        await LoadMessagesAsync(vm.Id);
    }

    private void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = SendMessageAsync();
        }
    }

    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        if (_isProcessing) return;
        if (_activeThreadId == null) return;

        var text = MessageInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        FileLog.Write($"[QuickActionsView] SendMessageAsync: threadId={_activeThreadId}, textLen={text.Length}");

        // Clear input immediately
        MessageInput.Text = "";

        // Show user bubble
        _chatBubbles.Add(CreateBubble("user", text));

        // Show thinking indicator
        var thinkingBubble = new ChatBubbleViewModel
        {
            Content = "Thinking...",
            Role = "assistant",
            BubbleBackground = ThinkingBubbleBrush,
            TextColor = DimTextBrush,
            Alignment = HorizontalAlignment.Left
        };
        _chatBubbles.Add(thinkingBubble);

        ScrollToBottom();

        _isProcessing = true;
        BtnSend.IsEnabled = false;
        _processingCts = new CancellationTokenSource();

        try
        {
            // If this is the first message, auto-generate title
            var isFirstMessage = _chatBubbles.Count <= 2; // user + thinking
            if (isFirstMessage)
            {
                _ = AutoTitleThreadAsync(_activeThreadId, text);
            }

            var response = await _service!.ExecuteAsync(_activeThreadId, text, _processingCts.Token);

            // Replace thinking bubble with actual response
            var thinkingIndex = _chatBubbles.IndexOf(thinkingBubble);
            if (thinkingIndex >= 0)
            {
                _chatBubbles[thinkingIndex] = CreateBubble("assistant", response);
            }

            // Update thread preview in list
            UpdateThreadPreview(_activeThreadId, response);

            ScrollToBottom();
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[QuickActionsView] SendMessageAsync: cancelled");
            _chatBubbles.Remove(thinkingBubble);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[QuickActionsView] SendMessageAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            FileLog.Write($"[QuickActionsView] SendMessageAsync stack: {ex.StackTrace}");
            if (ex.InnerException != null)
                FileLog.Write($"[QuickActionsView] SendMessageAsync inner: {ex.InnerException.Message}");

            // Replace thinking bubble with error
            var errorText = $"Error: {ex.Message}";
            var thinkingIndex = _chatBubbles.IndexOf(thinkingBubble);
            if (thinkingIndex >= 0)
            {
                _chatBubbles[thinkingIndex] = new ChatBubbleViewModel
                {
                    Content = errorText,
                    Role = "assistant",
                    BubbleBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x5C, 0x1A, 0x1A))),
                    TextColor = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))),
                    Alignment = HorizontalAlignment.Left
                };
            }

            // Also store the error in the DB so it persists
            _db!.AddMessage(_activeThreadId!, "assistant", errorText);
        }
        finally
        {
            _isProcessing = false;
            BtnSend.IsEnabled = true;
            _processingCts?.Dispose();
            _processingCts = null;
        }
    }

    private async Task AutoTitleThreadAsync(string threadId, string firstMessage)
    {
        try
        {
            var title = await _service!.GenerateTitleAsync(firstMessage);
            _db!.RenameThread(threadId, title);

            // Update UI
            _ = Dispatcher.BeginInvoke(() =>
            {
                var vm = FindThreadVm(threadId);
                if (vm != null)
                    vm.Title = title;
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[QuickActionsView] AutoTitleThreadAsync FAILED: {ex.Message}");
        }
    }

    private async void MenuRenameThread_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        if (menuItem.DataContext is not ThreadViewModel vm) return;

        // Simple input dialog using a prompt
        var dialog = new InputDialog("Rename Thread", "Thread title:", vm.Title);
        dialog.Owner = Window.GetWindow(this);
        if (dialog.ShowDialog() != true) return;

        var newTitle = dialog.InputText.Trim();
        if (string.IsNullOrEmpty(newTitle)) return;

        FileLog.Write($"[QuickActionsView] RenameThread: id={vm.Id}, newTitle={newTitle}");

        await Task.Run(() => _db!.RenameThread(vm.Id, newTitle));
        vm.Title = newTitle;
    }

    private async void MenuDeleteThread_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        if (menuItem.DataContext is not ThreadViewModel vm) return;

        var result = MessageBox.Show(
            $"Delete thread \"{vm.Title}\"? This cannot be undone.",
            "Delete Thread",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        FileLog.Write($"[QuickActionsView] DeleteThread: id={vm.Id}");

        await Task.Run(() => _db!.DeleteThread(vm.Id));
        _threads.Remove(vm);

        if (_activeThreadId == vm.Id)
        {
            _activeThreadId = null;
            _chatBubbles.Clear();
            ChatPlaceholder.Visibility = Visibility.Visible;
            ChatScrollViewer.Visibility = Visibility.Collapsed;
            InputBar.Visibility = Visibility.Collapsed;
        }
    }

    // -- Helpers --

    private ChatBubbleViewModel CreateBubble(string role, string content)
    {
        return new ChatBubbleViewModel
        {
            Content = content,
            Role = role,
            BubbleBackground = role == "user" ? UserBubbleBrush : AssistantBubbleBrush,
            TextColor = role == "user" ? WhiteBrush : TextBrush,
            Alignment = role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(() => ChatScrollViewer.ScrollToEnd());
    }

    private void UpdateThreadPreview(string threadId, string lastMessage)
    {
        var vm = FindThreadVm(threadId);
        if (vm == null) return;

        var preview = lastMessage;
        if (preview.Length > 80)
            preview = preview[..77] + "...";
        vm.Preview = preview;

        // Move thread to top of list
        var index = _threads.IndexOf(vm);
        if (index > 0)
            _threads.Move(index, 0);
    }

    private ThreadViewModel? FindThreadVm(string threadId)
    {
        return _threads.FirstOrDefault(t => t.Id == threadId);
    }
}

// -- View Models --

public sealed class ThreadViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private string _title = "";
    private string _preview = "";

    public required string Id { get; init; }
    public required DateTime UpdatedAt { get; set; }

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
        }
    }

    public string Preview
    {
        get => _preview;
        set
        {
            _preview = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChatBubbleViewModel
{
    public required string Content { get; init; }
    public required string Role { get; init; }
    public required SolidColorBrush BubbleBackground { get; init; }
    public required SolidColorBrush TextColor { get; init; }
    public required HorizontalAlignment Alignment { get; init; }
}
