using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Simple chat-bubble view layered on top of the existing ConPTY session.
/// Attach/Detach pattern following GitChangesControl.
/// </summary>
public partial class SimpleChatView : UserControl
{
    private Session? _session;
    private long _bufferPosition;
    private DispatcherTimer? _progressTimer;
    private ClaudeClient? _claudeClient;
    private CancellationTokenSource? _progressCts;
    private bool _summarizing;

    private readonly ObservableCollection<ChatMessageViewModel> _messages = new();

    public SimpleChatView()
    {
        InitializeComponent();
        ChatItems.ItemsSource = _messages;
    }

    /// <summary>Attach to a session and start monitoring.</summary>
    public void Attach(Session session)
    {
        FileLog.Write($"[SimpleChatView] Attach: session={session.Id}");
        Detach();

        _session = session;
        _bufferPosition = session.Buffer?.TotalBytesWritten ?? 0;

        // Load existing chat history
        foreach (var msg in session.ChatHistory.GetMessages())
            _messages.Add(new ChatMessageViewModel(msg));

        UpdateEmptyState();

        // Subscribe to new messages
        session.ChatHistory.MessageAdded += OnChatMessageAdded;

        // Subscribe to activity state changes for progress bar
        session.OnActivityStateChanged += OnActivityStateChanged;

        // Start progress timer (30-second interval)
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _progressTimer.Tick += ProgressTimer_Tick;

        // If already working, start the timer and show progress bar
        if (session.ActivityState == ActivityState.Working)
        {
            ProgressArea.Visibility = Visibility.Visible;
            _progressTimer.Start();
        }

        ScrollToBottom();
    }

    /// <summary>Detach from the current session and clean up.</summary>
    public void Detach()
    {
        if (_session != null)
        {
            FileLog.Write($"[SimpleChatView] Detach: session={_session.Id}");
            _session.ChatHistory.MessageAdded -= OnChatMessageAdded;
            _session.OnActivityStateChanged -= OnActivityStateChanged;
        }

        _progressTimer?.Stop();
        _progressTimer = null;
        _progressCts?.Cancel();
        _progressCts = null;
        _summarizing = false;
        _session = null;
        _bufferPosition = 0;
        _messages.Clear();
        ProgressArea.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Set the ClaudeClient to use for Haiku summarization.
    /// Called from MainWindow after creating the client.
    /// </summary>
    public void SetClaudeClient(ClaudeClient? client)
    {
        _claudeClient = client;
    }

    private void OnChatMessageAdded(ChatMessage message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _messages.Add(new ChatMessageViewModel(message));
            UpdateEmptyState();
            ScrollToBottom();
        });
    }

    private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (newState == ActivityState.Working)
            {
                ProgressArea.Visibility = Visibility.Visible;
                ProgressText.Text = "Claude is working...";
                _progressTimer?.Start();
            }
            else
            {
                ProgressArea.Visibility = Visibility.Collapsed;
                _progressTimer?.Stop();
                _progressCts?.Cancel();
                _progressCts = null;
                _summarizing = false;
            }
        });
    }

    private async void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (_session == null || _session.ActivityState != ActivityState.Working)
            return;

        if (_summarizing || _claudeClient == null)
            return;

        // Read terminal output since last position
        var terminalText = ReadTerminalSinceLastPosition();
        if (string.IsNullOrWhiteSpace(terminalText))
            return;

        _summarizing = true;
        _progressCts?.Cancel();
        _progressCts = new CancellationTokenSource();
        var ct = _progressCts.Token;

        try
        {
            var summary = await Task.Run(
                () => SimpleChatSummarizer.SummarizeProgressAsync(_claudeClient, terminalText, ct), ct);

            if (ct.IsCancellationRequested || _session == null)
                return;

            _session.ChatHistory.AddMessage(new ChatMessage(ChatMessageType.Status, summary));
            ProgressText.Text = summary;
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[SimpleChatView] ProgressTimer_Tick: cancelled (detach)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SimpleChatView] ProgressTimer_Tick FAILED: {ex.Message}");
        }
        finally
        {
            _summarizing = false;
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private async Task SendMessageAsync()
    {
        var text = ChatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _session == null)
            return;

        FileLog.Write($"[SimpleChatView] SendMessageAsync: textLen={text.Length}");

        // Add user bubble immediately
        _session.ChatHistory.AddMessage(new ChatMessage(ChatMessageType.User, text));

        // Clear input
        ChatInput.Clear();

        // Reset buffer position so we capture output from this point forward
        _bufferPosition = _session.Buffer?.TotalBytesWritten ?? 0;

        // Send to terminal
        await _session.SendTextAsync(text);
    }

    private string ReadTerminalSinceLastPosition()
    {
        if (_session?.Buffer == null)
            return string.Empty;

        var (data, newPosition) = _session.Buffer.GetWrittenSince(_bufferPosition);
        _bufferPosition = newPosition;

        if (data.Length == 0)
            return string.Empty;

        var raw = Encoding.UTF8.GetString(data);
        return TerminalOutputParser.StripAnsi(raw);
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _messages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ChatScroller.ScrollToEnd();
        });
    }
}
