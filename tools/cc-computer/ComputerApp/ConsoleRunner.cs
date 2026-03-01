using System.IO;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using CCComputer.Agent;

namespace CCComputer.App;

/// <summary>
/// CLI mode bootstrap and REPL for CC Computer.
/// Mirrors MainViewModel agent setup but outputs to the console.
/// </summary>
public class ConsoleRunner
{
    private CancellationTokenSource? _currentCts;

    public async Task RunAsync(string? singleCommand)
    {
        ConfigureLogging();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("========================================");
        Console.WriteLine("  CC Computer — CLI Mode");
        Console.WriteLine("========================================");
        Console.ResetColor();
        Console.WriteLine();

        // Load configuration (same pattern as MainViewModel)
        IConfiguration config;
        try
        {
            config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();
        }
        catch (Exception ex)
        {
            WriteError($"Failed to load appsettings.json: {ex.Message}");
            return;
        }

        var ccClickPath = config["Desktop:CcClickPath"];
        var apiKey = config["LLM:ApiKey"];
        var modelId = config["LLM:ModelId"] ?? "gpt-4o";
        var userName = config["User:Name"] ?? "User";

        // Fallback to environment variable (process-level, then user-level registry)
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            WriteError("No API key configured. Set LLM:ApiKey in appsettings.json or OPENAI_API_KEY env var.");
            return;
        }

        // Create session logger
        ActivityLogger? sessionLogger = null;
        try
        {
            sessionLogger = new ActivityLogger();
            WriteInfo($"Session log: {sessionLogger.SessionFolder}");
        }
        catch (Exception ex)
        {
            WriteError($"Failed to create session logger: {ex.Message}");
        }

        // Create agent
        ComputerControlAgent agent;
        try
        {
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

            agent = new ComputerControlAgent(agentConfig, sessionLogger);
            agent.RegisterPlugins();

            // Wire events to colored console output
            agent.OnThinking += msg =>
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  [think] {msg}");
                Console.ResetColor();
            };

            agent.OnToolCall += (name, args) =>
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"  [tool]  {name}");
                if (!string.IsNullOrEmpty(args))
                {
                    var truncated = args.Length > 120 ? args[..120] + "..." : args;
                    Console.Write($": {truncated}");
                }
                Console.WriteLine();
                Console.ResetColor();
            };

            agent.OnToolResult += (name, success, result) =>
            {
                Console.ForegroundColor = success ? ConsoleColor.DarkGreen : ConsoleColor.Red;
                var truncated = result.Length > 200 ? result[..200] + "..." : result;
                Console.WriteLine($"  [{(success ? "ok" : "FAIL")}]   {truncated}");
                Console.ResetColor();
            };

            agent.OnResponse += msg =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine();
                Console.WriteLine($"  {msg}");
                Console.ResetColor();
                Console.WriteLine();
            };

            agent.OnError += msg =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [ERROR] {msg}");
                Console.ResetColor();
            };

            WriteInfo($"Agent ready. Model: {modelId}");
        }
        catch (Exception ex)
        {
            WriteError($"Failed to initialize agent: {ex.Message}");
            sessionLogger?.Dispose();
            return;
        }

        Console.WriteLine();

        try
        {
            if (singleCommand != null)
            {
                // Single-shot mode: execute one command and exit
                await ExecuteCommandAsync(agent, singleCommand);
            }
            else
            {
                // Interactive REPL mode
                await RunReplAsync(agent);
            }
        }
        finally
        {
            // Generate HTML report before disposing the logger
            if (sessionLogger != null)
            {
                try
                {
                    var reportPath = SessionReporter.GenerateReport(sessionLogger.SessionFolder);
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine($"  Session report: {reportPath}");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  (Could not generate report: {ex.Message})");
                    Console.ResetColor();
                }
            }

            sessionLogger?.Dispose();
            Log.CloseAndFlush();
        }
    }

    private async Task RunReplAsync(ComputerControlAgent agent)
    {
        WriteInfo("Type a command and press Enter. Type \"quit\" to exit.");
        Console.WriteLine();

        // Ctrl+C cancels the current request, not the REPL
        Console.CancelKeyPress += (_, e) =>
        {
            if (_currentCts != null && !_currentCts.IsCancellationRequested)
            {
                e.Cancel = true; // Don't kill the process
                _currentCts.Cancel();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine("  [cancelled]");
                Console.ResetColor();
            }
        };

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("CC> ");
            Console.ResetColor();

            var input = Console.ReadLine();
            if (input == null) break; // EOF / pipe closed

            input = input.Trim();
            if (string.IsNullOrEmpty(input)) continue;
            if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                agent.ClearHistory();
                WriteInfo("Chat history cleared.");
                continue;
            }

            await ExecuteCommandAsync(agent, input);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Goodbye.");
        Console.ResetColor();
    }

    private async Task ExecuteCommandAsync(ComputerControlAgent agent, string command)
    {
        _currentCts = new CancellationTokenSource();
        try
        {
            await agent.ProcessRequestAsync(command, _currentCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Already printed "[cancelled]" from the Ctrl+C handler
        }
        catch (Exception ex)
        {
            WriteError($"Request failed: {ex.Message}");
            Log.Error(ex, "CLI request failed");
        }
        finally
        {
            _currentCts.Dispose();
            _currentCts = null;
        }
    }

    private static void ConfigureLogging()
    {
        // File-only logging for CLI mode — agent events already print to console
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CCComputer",
            "logs");

        Directory.CreateDirectory(logDirectory);

        var logFilePath = Path.Combine(logDirectory, "cc_computer_.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.WithProperty("Application", "CCComputer-CLI")
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static void WriteInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  {message}");
        Console.ResetColor();
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [ERROR] {message}");
        Console.ResetColor();
    }
}
