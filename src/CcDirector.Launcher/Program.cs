using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using Avalonia;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

public static class Program
{
    // Session-scoped (not Global\) mutex: one launcher per logged-in user session.
    // A second launch on the same port (e.g. autostart racing a manual start) sees the
    // mutex held and exits without trying to bind the port a second time.
    private static string SingleInstanceMutexName => $"CcDirector.Launcher.SingleInstance.{LauncherAppOptions.Port}";

    [STAThread]
    public static int Main(string[] args)
    {
        FileLog.Start();

        // Detached self-update helper mode: this process is a STAGED copy of the new Launcher exe.
        // It asks the running tray app to exit (POST /shutdown), swaps itself into the installed
        // location, relaunches, and verifies the new build is healthy - rolling back to the .old
        // build (and pinning the bad version) if not. NEVER the normal startup path: it exits when
        // done. Launched by LauncherUpdater.LaunchDetachedUpdater.
        if (Array.IndexOf(args, "--apply-update") >= 0)
            return ApplyUpdate(args);

        LauncherAppOptions.Parse(args);

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            FileLog.Write("[Program] CC Launcher already running in this session; exiting second instance.");
            FileLog.Stop();
            return 0;
        }

        FileLog.Write($"[Program] CC Launcher starting (port={LauncherAppOptions.Port}), log: {FileLog.CurrentLogPath}");

        // TRAY MODE MUST ANSWER SIGTERM TOO.
        //
        // Headless mode has handled it from the start (see RunHeadless), and tray mode never did -
        // so the launcher a person actually runs ignored the one signal every tool uses to ask a
        // process to stop. The orphan investigated on 2026-07-29 was a tray-mode instance: it
        // ignored SIGTERM and had to be killed. launchd bootout sends SIGTERM, our own uninstaller
        // asks politely first, and an operator at a terminal reaches for kill before kill -9; a
        // process that ignores all three is unmanageable by design rather than by accident.
        //
        // Shutting the classic desktop lifetime down is what lets the normal exit path run, so the
        // Gateway is unregistered and the log is closed instead of the process simply vanishing.
        using var sigtermTray = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            ctx =>
            {
                ctx.Cancel = true;
                FileLog.Write("[Program] SIGTERM received - shutting the tray launcher down");
                RequestTrayShutdown();
            });

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (!App.FrameworkInitialized)
        {
            // The user-interface platform itself could not initialize - on macOS this happens
            // when the launcher starts while the screen is locked (the window server refuses a
            // render timer). An unattended supervisor must not die because an icon cannot be
            // drawn: fall back to HEADLESS mode - web host, Gateway registration, and command
            // stream all run; only the tray icon is missing. /healthz reports
            // userInterface=degraded so the Gateway can see the difference. This fallback is
            // ONLY for platform initialization failures (before any App code ran); a fault in
            // the running app still exits loudly below.
            FileLog.Write($"[Program] User-interface platform failed to initialize: {ex.Message}");
            FileLog.Write("[Program] DEGRADED: running HEADLESS (no tray icon) - web host + Gateway stream only");
            return RunHeadless();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] FATAL: {ex}");
            return 1;
        }
        finally
        {
            _shutdownCompleted = true;
            FileLog.Write("[Program] CC Launcher exited");
            FileLog.Stop();
        }
    }

    /// <summary>
    /// Ask the running tray application to shut down, from a signal handler. Marshalled onto the
    /// user-interface thread because that is the only thread allowed to stop the lifetime; if the
    /// application is not up yet (a signal during startup) the process exits directly, which is the
    /// honest thing to do rather than ignoring the signal.
    /// </summary>
    /// <summary>Set when the normal exit path has finished, so the SIGTERM watchdog stands down instead
    /// of force-exiting a shutdown that is working.</summary>
    private static volatile bool _shutdownCompleted;

    private static void RequestTrayShutdown()
    {
        // Deliberately NOT a blocking call on the user-interface thread. Asking the desktop lifetime to
        // shut down runs the tray controller's disposal, which waits synchronously on an asynchronous
        // stop - so posting Shutdown to that thread and waiting could wedge the process precisely where
        // this handler is supposed to make it stoppable. Signal handlers must not be able to hang.
        //
        // So: ask nicely on a background thread, give it a bounded moment, then exit regardless. A
        // launcher that will not stop cleanly still stops.
        _ = Task.Run(() =>
        {
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => lifetime.Shutdown());
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[Program] SIGTERM: could not ask the lifetime to shut down: {ex.Message}");
            }
        });

        // The watchdog exists so a wedged shutdown cannot ignore the signal for ever - but it must not
        // cut a clean shutdown short either. The downstream steps have their own bounds (a Gateway
        // unregister allows five seconds, the host stop two, on top of an unbounded stream stop), so the
        // budget is generous and the exit is cancelled the moment shutdown actually finishes.
        _ = Task.Run(async () =>
        {
            for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(25); waited += TimeSpan.FromMilliseconds(250))
            {
                if (_shutdownCompleted) return;
                await Task.Delay(250);
            }
            FileLog.Write("[Program] SIGTERM: shutdown did not complete in 25s - exiting anyway");
            FileLog.Stop();
            Environment.Exit(0);
        });
    }

    /// <summary>
    /// Headless degraded mode: everything except the tray icon. Runs until a /shutdown
    /// request arrives on the web host or the process receives SIGTERM/SIGINT (launchd
    /// bootout sends SIGTERM; the graceful stop unregisters from the Gateway).
    /// </summary>
    private static int RunHeadless()
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task RequestShutdown() { shutdown.TrySetResult(); return Task.CompletedTask; }

        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; FileLog.Write("[Program] SIGTERM received"); shutdown.TrySetResult(); });
        using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT,
            ctx => { ctx.Cancel = true; FileLog.Write("[Program] SIGINT received"); shutdown.TrySetResult(); });

        using var lifetime = new CancellationTokenSource();
        var core = new LauncherCore(LauncherAppOptions.Port, LauncherCore.ReadVersion());
        try
        {
            core.StartAsync(RequestShutdown, userInterfaceState: "degraded").GetAwaiter().GetResult();
            LauncherCore.RegisterAutostartSafe();
            if (LauncherAppOptions.Managed)
                _ = LauncherCore.RunUpdateLoopAsync(lifetime.Token);

            FileLog.Write($"[Program] Headless launcher running on :{LauncherAppOptions.Port}");
            shutdown.Task.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] Headless FATAL: {ex}");
            return 1;
        }
        finally
        {
            lifetime.Cancel();
            core.StopAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Read the running Launcher's bearer token so the self-update helper can authenticate its
    /// POST /shutdown (issue #1609). READ-ONLY on purpose: LoadOrCreateToken would MINT one when the file
    /// is absent, and a freshly minted token is exactly the one the already-running Launcher does not
    /// know - it would 401 just like sending none. Null means "no token to read", so the caller can say
    /// that instead of blaming the exe lock.
    /// </summary>
    private static string? TryReadLauncherToken()
    {
        try
        {
            var path = LauncherAuth.TokenFile;
            if (!File.Exists(path))
            {
                FileLog.Write($"[Program] launcher token file not found at {path}");
                return null;
            }
            var token = File.ReadAllText(path).Trim();
            if (token.Length == 0)
            {
                FileLog.Write($"[Program] launcher token file is empty at {path}");
                return null;
            }
            return token;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] could not read the launcher token: {ex.Message}");
            return null;
        }
    }

    private static int ApplyUpdate(string[] args)
    {
        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        var target = Arg("--target");
        var version = Arg("--new-version");
        var port = int.TryParse(Arg("--port"), out var p) ? p : LauncherAppOptions.DefaultPort;
        // Relaunch arguments: the installed Launcher always relaunches managed; the self-update
        // test harness overrides this to keep its throwaway instance off the real install.
        var relaunchArgs = Arg("--args");
        if (relaunchArgs.Length == 0) relaunchArgs = LauncherTrayInstaller.InstalledArguments;
        var stagedSelf = Environment.ProcessPath ?? "";
        FileLog.Write($"[Program] --apply-update: version={version}, target={target}, port={port}, args={relaunchArgs}, staged={stagedSelf}");

        using var http = new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(5) };

        var result = new LauncherSelfUpdate().ApplyAsync(
            target, stagedSelf, version,
            stopLauncher: () =>
            {
                // Issue #1609: /shutdown is behind LauncherHost's token gate, so this MUST authenticate.
                // Sent token-less it was answered 401, the Launcher never exited, its exe never unlocked,
                // and the self-update aborted with the misleading "exe still locked after stop". The
                // exe-writability wait below is only a barrier if the process actually exits; it cannot
                // substitute for it. Same defect, same fix, as the Gateway helper.
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/shutdown");
                var token = TryReadLauncherToken();
                if (token is null)
                {
                    FileLog.Write("[Program] /shutdown SKIPPED: no launcher token readable; cannot ask the Launcher to exit, so the swap will abort");
                    return false;
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                try
                {
                    using var resp = http.SendAsync(request).GetAwaiter().GetResult();
                    FileLog.Write($"[Program] /shutdown -> {(int)resp.StatusCode}");
                    if (!resp.IsSuccessStatusCode)
                        FileLog.Write($"[Program] /shutdown REFUSED ({(int)resp.StatusCode}); the Launcher will not exit and the swap will abort on a locked exe");
                    return resp.IsSuccessStatusCode;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[Program] /shutdown unreachable ({ex.Message}); launcher presumably not running");
                    return false;
                }
            },
            startLauncher: () =>
            {
                try
                {
                    // UseShellExecute=true so the relaunched Launcher does NOT inherit this helper's
                    // stdio handles - an inherited stdout pipe keeps the caller's pipe open for the
                    // Launcher's whole lifetime (observed as a hang in any script that pipes output).
                    var psi = new ProcessStartInfo
                    {
                        FileName = target,
                        Arguments = relaunchArgs,
                        WorkingDirectory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
                        UseShellExecute = true,
                    };
                    using var proc = Process.Start(psi);
                    FileLog.Write($"[Program] relaunched Launcher pid={proc?.Id}");
                    return proc is not null;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[Program] relaunch FAILED: {ex.Message}");
                    return false;
                }
            },
            isHealthy: async ct =>
            {
                try { return (await http.GetAsync($"http://127.0.0.1:{port}/healthz", ct)).IsSuccessStatusCode; }
                catch { return false; }
            },
            healthTimeout: TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

        FileLog.Write($"[Program] self-update outcome={result.Outcome}: {result.Message}");
        foreach (var step in result.Steps) FileLog.Write($"[Program]   {step}");
        FileLog.Stop();
        return result.Outcome == SelfUpdateOutcome.Updated ? 0 : 1;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The launcher is a tray-only app: on macOS it lives in the menu bar and must
            // not occupy the Dock or the application switcher.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
