using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirectorSetup.Services;
using Microsoft.Win32;

namespace CcDirectorSetup.Steps;

public partial class CompleteStep : UserControl
{
    private readonly string _installPath = "";

    public CompleteStep()
    {
        InitializeComponent();
    }

    public CompleteStep(int installed, int skipped, string installPath, bool isUpdate, bool alreadyUpToDate = false)
    {
        InitializeComponent();
        _installPath = installPath;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;

        if (alreadyUpToDate)
        {
            HeadingText.Text = "Already Up to Date";
            DescriptionText.Text = "CC Director is already running the latest version.";
            PathNote.IsVisible = false;
        }
        else if (isUpdate)
        {
            HeadingText.Text = "Update Complete";
            DescriptionText.Text = "CC Director tools have been updated successfully.";
            PathNote.IsVisible = false;
        }

        SetupLog.Write($"[CompleteStep] Created: installed={installed}, skipped={skipped}, isUpdate={isUpdate}, alreadyUpToDate={alreadyUpToDate}");
    }

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] LaunchButton_Click");

        var binName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cc-director.exe"
            : "cc-director";
        var exePath = Path.Combine(_installPath, binName);

        if (!File.Exists(exePath))
        {
            SetupLog.Write($"[CompleteStep] cc-director not found at {exePath}");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var freshPath = GetFreshPathWindows();
                if (freshPath != null)
                    psi.Environment["PATH"] = freshPath;
            }

            Process.Start(psi);
            SetupLog.Write("[CompleteStep] LaunchButton_Click: cc-director launched");

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
