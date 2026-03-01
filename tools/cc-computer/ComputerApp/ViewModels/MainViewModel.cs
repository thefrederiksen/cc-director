using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Serilog;
using CCComputer.App.Models;
using CCComputer.Agent;
using CCComputer.Agent.Plugins;

namespace CCComputer.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Dispatcher? _dispatcher;
    private bool _disposed;

    // Lock for thread-safe log entry additions
    private readonly object _logLock = new object();

    // Agent for LLM-powered automation
    private ComputerControlAgent? _agent;
    private bool _agentReady;

    // Session logger - logs all activity to disk
    private readonly ActivityLogger? _sessionLogger;

    // Plugins for Phase 2 testing
    private readonly DesktopPlugin? _desktopPlugin;
    private readonly ScreenshotPlugin? _screenshotPlugin;
    private readonly ShellPlugin? _shellPlugin;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public MainViewModel()
    {
        Log.Debug("MainViewModel constructor starting");

        // Capture the dispatcher for UI thread operations
        _dispatcher = Application.Current?.Dispatcher;

        // Enable thread-safe collection binding - WPF will use our lock when accessing LogEntries
        // This prevents the "ItemsControl is inconsistent with its items source" error
        BindingOperations.EnableCollectionSynchronization(LogEntries, _logLock);

        // Create session logger - persists all activity to disk
        // Session continues until app closes, then a new session on next launch
        try
        {
            _sessionLogger = new ActivityLogger();
            Log.Information("Session logger created: {SessionFolder}", _sessionLogger.SessionFolder);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create session logger");
            AddLogEntry(LogEntry.Error($"Failed to create session logger: {ex.Message}"));
        }

        // Load configuration
        try
        {
            Log.Debug("Loading configuration");

            var config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            var ccClickPath = config["Desktop:CcClickPath"];
            var apiKey = config["LLM:ApiKey"];
            var modelId = config["LLM:ModelId"] ?? "gpt-4o";
            var userName = config["User:Name"] ?? "User";

            Log.Debug("Configuration loaded: ModelId={ModelId}, UserName={UserName}, CcClickPath={CcClickPath}",
                modelId, userName, ccClickPath);

            // Fallback to environment variable if no API key in config
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (!string.IsNullOrEmpty(apiKey))
                {
                    Log.Debug("Using API key from OPENAI_API_KEY environment variable");
                }
            }

            // Initialize plugins for manual testing
            if (!string.IsNullOrEmpty(ccClickPath) && File.Exists(ccClickPath))
            {
                Log.Debug("Initializing plugins with cc_click at {Path}", ccClickPath);
                _desktopPlugin = new DesktopPlugin(ccClickPath);
                _screenshotPlugin = new ScreenshotPlugin(ccClickPath);
                _shellPlugin = new ShellPlugin();
            }
            else
            {
                Log.Warning("cc_click not found at: {Path}", ccClickPath);
                AddLogEntry(LogEntry.Error($"cc_click not found at: {ccClickPath}"));
            }

            // Initialize agent if API key is configured
            if (!string.IsNullOrEmpty(apiKey))
            {
                Log.Information("Initializing agent with model {ModelId}", modelId);

                var agentConfig = new AgentConfig
                {
                    Provider = "OpenAI",
                    ModelId = modelId,
                    ApiKey = apiKey,
                    CcClickPath = ccClickPath ?? "",
                    UserName = userName,
                    EnableDetectionPipeline = config.GetValue("Detection:EnablePipeline", true),
                    EnableOcrDetection = config.GetValue("Detection:EnableOcr", true),
                    EnablePixelAnalysis = config.GetValue("Detection:EnablePixelAnalysis", true),
                    PythonPath = config["Detection:PythonPath"] ?? "python",
                    PixelDetectScriptPath = config["Detection:PixelDetectScriptPath"],
                };

                // Pass session logger to agent for persistent logging
                _agent = new ComputerControlAgent(agentConfig, _sessionLogger);
                _agent.RegisterPlugins();

                // Wire up agent events for UI display (these come from background threads!)
                _agent.OnThinking += msg => AddLogEntry(LogEntry.Think(msg));
                _agent.OnToolCall += (name, args) => AddLogEntry(LogEntry.Tool($"{name}{(args != null ? $": {args}" : "")}"));
                _agent.OnToolResult += (name, success, result) =>
                {
                    var truncated = result.Length > 200 ? result[..200] + "..." : result;
                    AddLogEntry(success ? LogEntry.Result(truncated) : LogEntry.Error(truncated));
                };
                _agent.OnResponse += msg => AddLogEntry(LogEntry.Result(msg));
                _agent.OnError += msg => AddLogEntry(LogEntry.Error(msg));

                _agentReady = true;

                var logPath = _sessionLogger?.SessionFolder ?? "(logging disabled)";
                AddLogEntry(LogEntry.Info($"CC Computer ready. Agent initialized with {modelId}."));
                AddLogEntry(LogEntry.Info($"Session log: {logPath}"));

                Log.Information("Agent initialized successfully");
            }
            else
            {
                Log.Warning("No API key configured - agent disabled");
                AddLogEntry(LogEntry.Info("CC Computer ready. No API key configured - agent disabled."));
                AddLogEntry(LogEntry.Info("Set LLM:ApiKey in appsettings.json to enable the agent."));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize MainViewModel");
            _sessionLogger?.LogError("Failed to initialize", ex);
            AddLogEntry(LogEntry.Error($"Failed to initialize: {ex.Message}"));
        }

        Log.Debug("MainViewModel constructor completed");
    }

    private bool CanSend => !string.IsNullOrWhiteSpace(InputText) && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText))
            return;

        var request = InputText;
        InputText = string.Empty;

        Log.Information("User request: {Request}", request);

        _cancellationTokenSource = new CancellationTokenSource();
        IsRunning = true;

        try
        {
            AddLogEntry(LogEntry.Start($"Processing: \"{request}\""));

            if (_agentReady && _agent != null)
            {
                // Use the LLM agent
                Log.Debug("Sending request to agent");
                var response = await _agent.ProcessRequestAsync(request, _cancellationTokenSource.Token);
                Log.Information("Agent completed request successfully");
                AddLogEntry(LogEntry.Done("Task completed."));
            }
            else
            {
                // No agent - just simulate for UI testing
                Log.Debug("No agent configured, running simulation");
                await SimulateAgentActivityAsync(request, _cancellationTokenSource.Token);
                AddLogEntry(LogEntry.Done("Simulation completed (no API key configured)."));
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Request cancelled by user");
            AddLogEntry(LogEntry.Stopped("Operation cancelled by user."));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Request failed: {Request}", request);
            AddLogEntry(LogEntry.Error($"Error: {ex.Message}"));
        }
        finally
        {
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private bool CanStop => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        Log.Information("User pressed STOP button");
        _cancellationTokenSource?.Cancel();
    }

    /// <summary>
    /// Add a log entry to the UI list. Thread-safe with proper locking.
    /// </summary>
    private void AddLogEntry(LogEntry entry)
    {
        // Use lock to prevent concurrent modifications that confuse WPF's binding
        lock (_logLock)
        {
            try
            {
                if (_dispatcher is null)
                {
                    // No dispatcher yet (during startup) - add directly
                    LogEntries.Add(entry);
                }
                else if (_dispatcher.CheckAccess())
                {
                    // Already on UI thread
                    LogEntries.Add(entry);
                }
                else
                {
                    // On background thread - marshal to UI thread synchronously
                    // The lock ensures only one thread can invoke at a time
                    _dispatcher.Invoke(() => LogEntries.Add(entry));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to add log entry to UI: {Message}", entry.Message);
            }
        }
    }

    /// <summary>
    /// Temporary simulation of agent activity for UI testing.
    /// Will be replaced with actual agent calls.
    /// </summary>
    private async Task SimulateAgentActivityAsync(string request, CancellationToken ct)
    {
        AddLogEntry(LogEntry.Think("Analyzing request..."));
        await Task.Delay(500, ct);

        AddLogEntry(LogEntry.Tool("desktop_screenshot"));
        await Task.Delay(300, ct);
        AddLogEntry(LogEntry.Result("Screenshot captured: 1920x1080"));

        await Task.Delay(500, ct);
        AddLogEntry(LogEntry.Think("I can see the desktop. Planning next steps..."));

        await Task.Delay(500, ct);
        AddLogEntry(LogEntry.Tool("desktop_list_windows"));
        await Task.Delay(300, ct);
        AddLogEntry(LogEntry.Result("Found 12 open windows"));

        await Task.Delay(500, ct);
        AddLogEntry(LogEntry.Think($"Processing request: {request}"));
    }

    // ========================================
    // Phase 2: Manual Tool Testing Commands
    // ========================================

    [RelayCommand]
    private async Task TestListWindowsAsync()
    {
        if (_desktopPlugin is null)
        {
            Log.Warning("TestListWindows called but desktop plugin not initialized");
            AddLogEntry(LogEntry.Error("Desktop plugin not initialized"));
            return;
        }

        Log.Debug("TestListWindows starting");
        AddLogEntry(LogEntry.Tool("list_windows"));
        try
        {
            var result = await _desktopPlugin.ListWindowsAsync();
            Log.Debug("TestListWindows completed: {ResultLength} chars", result.Length);
            AddLogEntry(LogEntry.Result(result.Length > 500 ? result[..500] + "..." : result));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TestListWindows failed");
            AddLogEntry(LogEntry.Error($"list_windows failed: {ex.Message}"));
        }
    }

    [RelayCommand]
    private async Task TestScreenshotAsync()
    {
        if (_screenshotPlugin is null)
        {
            Log.Warning("TestScreenshot called but screenshot plugin not initialized");
            AddLogEntry(LogEntry.Error("Screenshot plugin not initialized"));
            return;
        }

        Log.Debug("TestScreenshot starting");
        AddLogEntry(LogEntry.Tool("take_screenshot"));
        try
        {
            var result = await _screenshotPlugin.TakeScreenshotAsync();
            Log.Debug("TestScreenshot completed: {Result}", result);
            AddLogEntry(LogEntry.Result(result));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TestScreenshot failed");
            AddLogEntry(LogEntry.Error($"take_screenshot failed: {ex.Message}"));
        }
    }

    [RelayCommand]
    private async Task TestLaunchNotepadAsync()
    {
        if (_shellPlugin is null)
        {
            Log.Warning("TestLaunchNotepad called but shell plugin not initialized");
            AddLogEntry(LogEntry.Error("Shell plugin not initialized"));
            return;
        }

        Log.Debug("TestLaunchNotepad starting");
        AddLogEntry(LogEntry.Tool("launch_application: notepad"));
        try
        {
            var result = await _shellPlugin.LaunchApplicationAsync("notepad");
            Log.Debug("TestLaunchNotepad completed: {Result}", result);
            AddLogEntry(LogEntry.Result(result));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TestLaunchNotepad failed");
            AddLogEntry(LogEntry.Error($"launch_application failed: {ex.Message}"));
        }
    }

    [RelayCommand]
    private async Task TestListElementsAsync()
    {
        if (_desktopPlugin is null)
        {
            Log.Warning("TestListElements called but desktop plugin not initialized");
            AddLogEntry(LogEntry.Error("Desktop plugin not initialized"));
            return;
        }

        Log.Debug("TestListElements starting");
        AddLogEntry(LogEntry.Tool("list_elements: Notepad"));
        try
        {
            var result = await _desktopPlugin.ListElementsAsync("Notepad", depth: 10);
            Log.Debug("TestListElements completed: {ResultLength} chars", result.Length);
            AddLogEntry(LogEntry.Result(result.Length > 500 ? result[..500] + "..." : result));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TestListElements failed");
            AddLogEntry(LogEntry.Error($"list_elements failed: {ex.Message}"));
        }
    }

    [RelayCommand]
    private async Task TestClickAsync()
    {
        if (_desktopPlugin is null)
        {
            Log.Warning("TestClick called but desktop plugin not initialized");
            AddLogEntry(LogEntry.Error("Desktop plugin not initialized"));
            return;
        }

        Log.Debug("TestClick starting");
        AddLogEntry(LogEntry.Tool("click: File menu in Notepad"));
        try
        {
            var result = await _desktopPlugin.ClickAsync(windowTitle: "Notepad", elementName: "File");
            Log.Debug("TestClick completed: {Result}", result);
            AddLogEntry(LogEntry.Result(result));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TestClick failed");
            AddLogEntry(LogEntry.Error($"click failed: {ex.Message}"));
        }
    }

    [RelayCommand]
    private async Task TestTypeAsync()
    {
        if (_desktopPlugin is null)
        {
            Log.Warning("TestType called but desktop plugin not initialized");
            AddLogEntry(LogEntry.Error("Desktop plugin not initialized"));
            return;
        }

        Log.Debug("TestType starting");
        AddLogEntry(LogEntry.Tool("focus_window + send_keys: 'Hello from CC Computer!'"));
        try
        {
            // First focus the Notepad window
            var focusResult = await _desktopPlugin.FocusWindowAsync("Notepad");
            Log.Debug("Focus result: {Result}", focusResult);
            AddLogEntry(LogEntry.Result(focusResult));

            // Small delay to ensure focus
            await Task.Delay(200);

            // Then send keystrokes
            var typeResult = await _desktopPlugin.SendKeysAsync("Hello from CC Computer!");
            Log.Debug("Type result: {Result}", typeResult);
            AddLogEntry(LogEntry.Result(typeResult));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TestType failed");
            AddLogEntry(LogEntry.Error($"type failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Dispose the session logger when the app closes.
    /// This finalizes the session log file.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            Log.Debug("MainViewModel disposing");
            _sessionLogger?.Dispose();
            _disposed = true;
            Log.Debug("MainViewModel disposed");
        }
    }
}
