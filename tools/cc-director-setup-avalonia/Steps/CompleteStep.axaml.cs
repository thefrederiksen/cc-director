using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;
using Microsoft.Win32;

namespace CcDirectorSetup.Steps;

public partial class CompleteStep : UserControl
{
    private readonly string _installPath = "";
    private int _installed;
    private int _skipped;
    private bool _isUpdate;
    private IReadOnlyList<string> _skippedNames = [];

    public CompleteStep()
    {
        InitializeComponent();
    }

    public CompleteStep(int installed, int skipped, string installPath, bool isUpdate, bool alreadyUpToDate = false, string? version = null, string? capabilityNotice = null, IReadOnlyList<string>? skippedNames = null)
    {
        InitializeComponent();

        // Recommended prerequisites no longer block the wizard, so this is where the user is told
        // what they skipped and what it costs them - at the end, next to what they can do about it.
        if (!string.IsNullOrWhiteSpace(capabilityNotice))
        {
            CapabilityNoticeText.Text = capabilityNotice;
            CapabilityPanel.IsVisible = true;
            SetupLog.Write($"[CompleteStep] capability notice shown: {capabilityNotice}");
        }

        _installPath = installPath;
        _installed = installed;
        _skipped = skipped;
        _isUpdate = isUpdate;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;
        LogPathBox.Text = SetupLog.Path;

        var versionSuffix = string.IsNullOrEmpty(version) ? "" : $" · v{version.TrimStart('v')}";

        if (alreadyUpToDate)
        {
            HeadingText.Text = "✓  Already Up to Date";
            DescriptionText.Text = "DevThrottle is already running the latest version.";
            SummaryLine.Text = $"Nothing to do{versionSuffix}";
            PathNote.IsVisible = false;
        }
        else if (isUpdate)
        {
            HeadingText.Text = "✓  Update Complete";
            DescriptionText.Text = "Everything went perfectly. You're up to date.";
            SummaryLine.Text = $"{installed} components updated{versionSuffix}";
            PathNote.IsVisible = false;
        }
        else
        {
            SummaryLine.Text = $"{installed} components installed{versionSuffix}";
        }

        // Failure path: surface the problem loudly - amber heading, full summary box,
        // and the details/report expander forced open. On success all of that stays
        // out of the way behind the small collapsed expander at the bottom.
        if (skipped > 0)
        {
            _skippedNames = skippedNames ?? [];
            var amber = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
            HeadingText.Text = isUpdate ? "Update finished with problems" : "Setup finished with problems";
            HeadingText.Foreground = amber;
            // Name what failed - "1 component(s)" tells the user nothing actionable.
            var what = _skippedNames.Count switch
            {
                0 => skipped == 1 ? "One component" : $"{skipped} components",
                1 => _skippedNames[0],
                _ => string.Join(", ", _skippedNames),
            };
            DescriptionText.Text = $"{what} did not install. DevThrottle may still work, but please report this.";
            SummaryLine.IsVisible = false;
            FailurePanel.IsVisible = true;
            if (_skippedNames.Count > 0) SkippedText.Text = $"{skipped} ({string.Join(", ", _skippedNames)})";
            DetailsHeader.Text = $"{what} did not install - please report this";
            DetailsHeader.Foreground = amber;
            DetailsExpander.IsExpanded = true;
        }

        SetupLog.Write($"[CompleteStep] Created: installed={installed}, skipped={skipped}, isUpdate={isUpdate}, alreadyUpToDate={alreadyUpToDate}, version={version}");
    }

    private void OpenLogButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] OpenLogButton_Click");
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.Path}\"") { UseShellExecute = true });
            else
            {
                var psi = new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open");
                psi.ArgumentList.Add(SetupLog.Dir);
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] OpenLogButton_Click FAILED: {ex.Message}");
        }
    }

    private void ReportButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] ReportButton_Click");
        try
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Windows";
            var title = _skipped > 0
                ? $"[install] Setup failed on {os} ({_skipped} component(s) skipped)"
                : $"[install] Setup problem on {os}";
            IssueReporter.Open(IssueReporter.BuildUrl(title, BuildIssueBody(os)));
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] ReportButton_Click FAILED: {ex.Message}");
        }
    }

    private string BuildIssueBody(string os)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## What happened");
        sb.AppendLine("<!-- Briefly describe the problem. -->");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- Mode: {(_isUpdate ? "update" : "install")}");
        sb.AppendLine($"- OS: {os} ({RuntimeInformation.OSDescription})");
        sb.AppendLine($"- Arch: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"- Installed: {_installed}, Skipped: {_skipped}"
            + (_skippedNames.Count > 0 ? $" ({string.Join(", ", _skippedNames)})" : ""));
        sb.AppendLine();
        sb.AppendLine("## Setup log");
        sb.AppendLine($"Full log (please attach it): `{SetupLog.Path}`");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(ReadLogTail(160));
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string ReadLogTail(int lines)
    {
        try
        {
            var all = File.ReadAllLines(SetupLog.Path);
            var start = Math.Max(0, all.Length - lines);
            return string.Join("\n", all[start..]);
        }
        catch (Exception ex)
        {
            return $"(could not read log: {ex.Message})";
        }
    }

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] LaunchButton_Click");

        // _installPath is the canonical Director path (InstallLayout.PathFor). On Windows that is the
        // installed cc-director.exe; on macOS it is the ~/Applications/Director.app bundle. The two
        // launch differently: run the exe directly on Windows, but on macOS hand the bundle to
        // /usr/bin/open so LaunchServices registers it - that is what gives the app its Dock icon and
        // foreground activation. Launching the inner Mach-O binary directly gives neither.
        try
        {
            ProcessStartInfo psi;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (!Directory.Exists(_installPath))
                {
                    SetupLog.Write($"[CompleteStep] Director bundle not found at {_installPath}");
                    return;
                }

                psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add(_installPath);
            }
            else
            {
                if (!File.Exists(_installPath))
                {
                    SetupLog.Write($"[CompleteStep] cc-director not found at {_installPath}");
                    return;
                }

                psi = new ProcessStartInfo { FileName = _installPath, UseShellExecute = false };

                var freshPath = GetFreshPathWindows();
                if (freshPath != null)
                    psi.Environment["PATH"] = freshPath;
            }

            Process.Start(psi);
            SetupLog.Write("[CompleteStep] LaunchButton_Click: Director launched");

            // Close the setup wizard
            var window = this.VisualRoot as Window;
            window?.Close();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] LaunchButton_Click FAILED: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? GetFreshPath()
    {
        try
        {
            using var userKey = Registry.CurrentUser.OpenSubKey("Environment");
            var userPath = userKey?.GetValue("Path", "") as string ?? "";

            using var sysKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            var systemPath = sysKey?.GetValue("Path", "") as string ?? "";

            var combined = systemPath + ";" + userPath;
            SetupLog.Write("[CompleteStep] GetFreshPath: built fresh PATH from registry");
            return combined;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] GetFreshPath FAILED: {ex.Message}");
            return null;
        }
    }

    private static string? GetFreshPathWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetFreshPath();
        return null;
    }
}
