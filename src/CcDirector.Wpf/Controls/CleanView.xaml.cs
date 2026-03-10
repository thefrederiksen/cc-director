using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Rich card-based view of Claude Code session output.
/// Parses JSONL streaming output and renders each tool call as a styled widget card.
/// Follows Attach/Detach pattern from SimpleChatView.
/// </summary>
public partial class CleanView : UserControl
{
    private Session? _session;
    private DispatcherTimer? _pollTimer;
    private int _lastLineCount;
    private string? _jsonlPath;
    private bool _parsing;

    private readonly ObservableCollection<CleanWidgetViewModel> _widgets = new();
    private readonly object _widgetsLock = new();

    public CleanView()
    {
        InitializeComponent();
        WidgetItems.ItemsSource = _widgets;
        BindingOperations.EnableCollectionSynchronization(_widgets, _widgetsLock);
    }

    /// <summary>Attach to a session and start monitoring its JSONL output.</summary>
    public void Attach(Session session)
    {
        FileLog.Write($"[CleanView] Attach: session={session.Id}, backendType={session.BackendType}");
        Detach();

        _session = session;
        _lastLineCount = 0;

        // Subscribe to activity state changes
        session.OnActivityStateChanged += OnActivityStateChanged;

        if (session.Backend is StudioBackend studio)
        {
            // Studio mode: subscribe to live stream events, no file polling
            FileLog.Write("[CleanView] Attach: Studio mode -- subscribing to StreamMessageReceived");
            studio.StreamMessageReceived += OnStreamMessageReceived;
            LoadingText.Visibility = Visibility.Visible;
            EmptyText.Visibility = Visibility.Collapsed;

            // Load any messages already received
            var existing = studio.GetMessages();
            if (existing.Count > 0)
            {
                var widgets = CleanWidgetViewModel.BuildFromMessages(existing);
                _widgets.Clear();
                foreach (var w in widgets)
                    _widgets.Add(w);
                UpdateEmptyState();
                ScrollToBottom();
            }
        }
        else
        {
            // Terminal mode: file-based polling
            _jsonlPath = ResolveJsonlPath(session);

            if (_jsonlPath == null)
            {
                FileLog.Write("[CleanView] Attach: no JSONL path available yet, will poll");
                LoadingText.Visibility = Visibility.Visible;
                EmptyText.Visibility = Visibility.Collapsed;
            }
            else
            {
                LoadingText.Visibility = Visibility.Visible;
                EmptyText.Visibility = Visibility.Collapsed;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ParseAndUpdate());
            }

            // Subscribe to metadata changes (ClaudeSessionId may arrive later)
            session.OnClaudeMetadataChanged += OnClaudeMetadataChanged;

            // Start polling timer (2 second interval for incremental updates)
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();
        }

        // Show progress if already working
        if (session.ActivityState == ActivityState.Working)
        {
            ProgressArea.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Detach from the current session and clean up.</summary>
    public void Detach()
    {
        if (_session != null)
        {
            FileLog.Write($"[CleanView] Detach: session={_session.Id}");
            _session.OnActivityStateChanged -= OnActivityStateChanged;
            _session.OnClaudeMetadataChanged -= OnClaudeMetadataChanged;

            // Unsubscribe from StudioBackend events
            if (_session.Backend is StudioBackend studio)
                studio.StreamMessageReceived -= OnStreamMessageReceived;
        }

        _pollTimer?.Stop();
        _pollTimer = null;
        _session = null;
        _jsonlPath = null;
        _lastLineCount = 0;
        _parsing = false;
        _widgets.Clear();
        ProgressArea.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
    }

    private string? ResolveJsonlPath(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId))
            return null;

        var path = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
        if (!System.IO.File.Exists(path))
        {
            FileLog.Write($"[CleanView] ResolveJsonlPath: file not found: {path}");
            return null;
        }

        FileLog.Write($"[CleanView] ResolveJsonlPath: {path}");
        return path;
    }

    private void OnClaudeMetadataChanged(ClaudeSessionMetadata? metadata)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_session == null)
                return;

            // Try to resolve JSONL path now that metadata may have updated
            if (_jsonlPath == null)
            {
                _jsonlPath = ResolveJsonlPath(_session);
                if (_jsonlPath != null)
                {
                    FileLog.Write("[CleanView] OnClaudeMetadataChanged: JSONL path resolved, parsing");
                    ParseAndUpdate();
                }
            }
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
            }
            else
            {
                ProgressArea.Visibility = Visibility.Collapsed;

                // Do a final parse when Claude finishes a turn
                if (oldState == ActivityState.Working)
                {
                    ParseAndUpdate();
                }
            }
        });
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_session == null || _parsing)
            return;

        // Try to resolve path if we don't have it yet
        if (_jsonlPath == null)
        {
            _jsonlPath = ResolveJsonlPath(_session!);
            if (_jsonlPath == null)
                return;
        }

        ParseAndUpdate();
    }

    private void ParseAndUpdate()
    {
        if (_jsonlPath == null || _parsing)
            return;

        _parsing = true;

        try
        {
            var (newMessages, newLineCount) = StreamMessageParser.ParseFileFrom(_jsonlPath, _lastLineCount);

            if (newMessages.Count == 0 && _lastLineCount > 0)
            {
                // No new messages - nothing to do
                return;
            }

            if (_lastLineCount == 0)
            {
                // Full initial load
                var allMessages = StreamMessageParser.ParseFile(_jsonlPath);
                var allWidgets = CleanWidgetViewModel.BuildFromMessages(allMessages);

                _widgets.Clear();
                foreach (var w in allWidgets)
                    _widgets.Add(w);

                _lastLineCount = newLineCount > 0 ? newLineCount : CountLines(_jsonlPath);
            }
            else if (newMessages.Count > 0)
            {
                // Incremental update - rebuild all widgets from scratch
                // (needed because tool results reference earlier tool_use blocks)
                var allMessages = StreamMessageParser.ParseFile(_jsonlPath);
                var allWidgets = CleanWidgetViewModel.BuildFromMessages(allMessages);

                _widgets.Clear();
                foreach (var w in allWidgets)
                    _widgets.Add(w);

                _lastLineCount = newLineCount;
            }

            UpdateEmptyState();
            ScrollToBottom();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CleanView] ParseAndUpdate FAILED: {ex.Message}");
        }
        finally
        {
            _parsing = false;
        }
    }

    private static int CountLines(string path)
    {
        try
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using var reader = new System.IO.StreamReader(fs);

            int count = 0;
            while (reader.ReadLine() != null)
                count++;
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private void UpdateEmptyState()
    {
        LoadingText.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = _widgets.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            WidgetScroller.ScrollToEnd();
        });
    }

    /// <summary>Handle live stream messages from StudioBackend.</summary>
    private void OnStreamMessageReceived(StreamMessage msg)
    {
        // Called from background thread -- dispatch to UI
        Dispatcher.BeginInvoke(() =>
        {
            if (_session == null || _session.Backend is not StudioBackend studio)
                return;

            // Rebuild all widgets from the accumulated messages
            var allMessages = studio.GetMessages();
            var allWidgets = CleanWidgetViewModel.BuildFromMessages(allMessages);

            _widgets.Clear();
            foreach (var w in allWidgets)
                _widgets.Add(w);

            UpdateEmptyState();
            ScrollToBottom();
        });
    }
}
