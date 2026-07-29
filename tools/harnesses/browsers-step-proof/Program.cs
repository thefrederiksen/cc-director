// Browsers-step live proof for the mac-browser-detection branch (pull request 2278).
//
// Runs the REAL FirstRunWizardDialog headless with Skia on this Mac, navigates to the Browsers
// step, and captures PNG proof that the step offers browser setup instead of the red
// "Neither Chrome nor Edge is installed" banner. Then goes further: creates a browser through
// AutomationBrowserService.Create (the call whose ResolveInstalled used to throw on the broken
// detection), launches it, and hits its debug port.
//
// Storage is sandboxed via CC_DIRECTOR_ROOT so nothing touches the machine's real registry.
// Detection reads the REAL /Applications, which is the point of the proof.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CcDirector.Avalonia;
using CcDirector.Core.Browsers;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using IoPath = System.IO.Path;

internal static class Program
{
    private static string _outDir = "";
    private static int _shot;

    [STAThread]
    private static int Main(string[] args)
    {
        _outDir = Arg(args, "--out") ?? IoPath.Combine(AppContext.BaseDirectory, "proof");
        var root = Arg(args, "--root")
            ?? IoPath.Combine(IoPath.GetTempPath(), $"browsers-proof-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);

        Console.WriteLine($"proof out: {_outDir}");
        Console.WriteLine($"sandboxed CC_DIRECTOR_ROOT: {root}");
        Console.WriteLine();

        // ---- Fact 1: detection on this machine -------------------------------------------------
        var detected = BrowserLauncher.DetectBrowsers();
        Console.WriteLine($"DetectBrowsers() -> {detected.Count} browser(s):");
        foreach (var b in detected)
            Console.WriteLine($"  {b.Kind}: \"{b.DisplayName}\" exe={b.ExePath} userData={b.UserDataDir}");
        if (detected.Count == 0)
        {
            Console.WriteLine("FAIL: detection still returns nothing on this Mac");
            return 1;
        }
        Console.WriteLine();

        // ---- Fact 2: the wizard's Browsers step ------------------------------------------------
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var dialog = new FirstRunWizardDialog(new AgentOptions());
        dialog.Show();
        Pump();

        // Navigate straight to the Browsers step the way the dialog itself does: move the model,
        // then show the step (which kicks off the real machine-state refresh).
        var model = Field(dialog, "_model");
        model.GetType().GetMethod("GoTo")!.Invoke(model, new object[] { WizardStep.Browsers });
        typeof(FirstRunWizardDialog)
            .GetMethod("ShowStep", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(dialog, new object[] { WizardStep.Browsers });
        Pump();

        var rows = (Panel)Field(dialog, "BrowsersRowsPanel");
        var errorBox = (Control)Field(dialog, "BrowsersErrorBox");
        var primary = (Button)Field(dialog, "PrimaryButton");

        // Rows appear only when RefreshBrowsersScreenAsync has finished reading the machine.
        PumpUntil(() => rows.Children.Count >= 2, TimeSpan.FromSeconds(30), "the browsers state read");
        Capture(dialog, "browsers-step");
        Console.WriteLine($"Browsers step: errorBoxVisible={errorBox.IsVisible}, primaryButton=\"{primary.Content}\"");
        Console.WriteLine();

        // ---- Fact 3: Create() - the call that used to throw in ResolveInstalled -----------------
        AutomationBrowser created;
        try
        {
            created = AutomationBrowserService.Create("Agent browser", BrowserKind.Chrome);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: Create() threw: {ex.Message}");
            Capture(dialog, "create-failed");
            return 1;
        }
        Console.WriteLine($"Create() ok: id={created.Id}, kind={created.Kind}, port={created.Port}, dir={created.UserDataDir}");

        // Repaint the step so the PNG shows the created browser the way a user would see it.
        var refresh = (Task)typeof(FirstRunWizardDialog)
            .GetMethod("RefreshBrowsersScreenAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(dialog, null)!;
        PumpUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(30), "the post-create refresh");
        Pump();
        Capture(dialog, "browsers-step-created");
        Console.WriteLine($"After create: primaryButton=\"{primary.Content}\"");
        Console.WriteLine();

        // ---- Fact 4: launch it and hit the debug port -------------------------------------------
        var launch = Task.Run(() => AutomationBrowserService.LaunchAsync(created.Id));
        PumpUntil(() => launch.IsCompleted, TimeSpan.FromSeconds(60), "the browser launch");
        if (launch.Exception is not null)
        {
            Console.WriteLine($"FAIL: LaunchAsync threw: {launch.Exception.InnerException?.Message}");
            return 1;
        }
        Console.WriteLine($"LaunchAsync ok: port={launch.Result.Port}");

        var probe = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return await http.GetStringAsync($"http://127.0.0.1:{launch.Result.Port}/json/version");
        });
        PumpUntil(() => probe.IsCompleted, TimeSpan.FromSeconds(30), "the debug port probe");
        if (probe.Exception is not null)
        {
            Console.WriteLine($"FAIL: debug port probe threw: {probe.Exception.InnerException?.Message}");
            return 1;
        }
        Console.WriteLine($"debug port answered: {probe.Result.ReplaceLineEndings(" ").Trim()}");

        // The wizard paints "created" and "running" identically (signed-in is the state it cares
        // about), so a screenshot cannot evidence that the browser was up. The debug port response
        // is the evidence - keep it as a text artifact beside the screenshots.
        var cdpProofPath = IoPath.Combine(_outDir, "cdp-json-version.txt");
        File.WriteAllText(cdpProofPath,
            $"GET http://127.0.0.1:{launch.Result.Port}/json/version at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n"
            + $"(browser id={created.Id}, launched by AutomationBrowserService.LaunchAsync on this Mac)\n\n"
            + probe.Result + "\n");
        Console.WriteLine($"saved {IoPath.GetFileName(cdpProofPath)}");

        // ---- Clean up: stop the browser we started, remove the sandboxed record -----------------
        var stop = Task.Run(() => AutomationBrowserService.StopAsync(created.Id));
        PumpUntil(() => stop.IsCompleted, TimeSpan.FromSeconds(30), "the browser stop");
        var remove = Task.Run(() => AutomationBrowserService.RemoveAsync(created.Id));
        PumpUntil(() => remove.IsCompleted, TimeSpan.FromSeconds(30), "the record removal");
        Console.WriteLine($"cleanup: stop faulted={stop.Exception is not null}, remove faulted={remove.Exception is not null}");

        Console.WriteLine();
        Console.WriteLine($"PROOF OK -> {_outDir}");
        return 0;
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(target)
        ?? throw new InvalidOperationException($"field {name} not found or null");

    private static void Pump(int ticks = 5)
    {
        for (var i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out waiting for {what}");
            Pump();
            Thread.Sleep(50);
        }
        Pump();
    }

    private static void Capture(Window w, string name)
    {
        var frame = w.CaptureRenderedFrame();
        if (frame is null) { Console.WriteLine($"WARN: no frame for {name}"); return; }
        _shot++;
        var path = IoPath.Combine(_outDir, $"{_shot:D2}-{name}.png");
        frame.Save(path);
        Console.WriteLine($"saved {IoPath.GetFileName(path)}");
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
