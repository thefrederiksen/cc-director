using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using Serilog.Events;

namespace CCComputer.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Configure Serilog FIRST - before anything else
        ConfigureLogging();

        Log.Information("CC Computer starting up");

        base.OnStartup(e);

        // Handle unhandled exceptions on the UI thread
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        // Handle unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // Handle unhandled exceptions in async code
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        Log.Information("CC Computer startup complete");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("CC Computer shutting down");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void ConfigureLogging()
    {
        // Create logs directory in AppData
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
            .Enrich.WithProperty("Application", "CCComputer")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Log only - no MessageBox
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Log only - no MessageBox
        var ex = e.ExceptionObject as Exception;
        Log.Fatal(ex, "Fatal unhandled exception - IsTerminating={IsTerminating}", e.IsTerminating);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Log only - no MessageBox
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
