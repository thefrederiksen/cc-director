using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class CompleteStep : UserControl
{
    private readonly string _installPath;

    public CompleteStep(int installed, int skipped, string installPath)
    {
        InitializeComponent();
        _installPath = installPath;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;
        SetupLog.Write($"[CompleteStep] Created: installed={installed}, skipped={skipped}");
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] LaunchButton_Click");

        var exePath = Path.Combine(_installPath, "cc-director.exe");
        if (!File.Exists(exePath))
        {
            SetupLog.Write($"[CompleteStep] cc-director.exe not found at {exePath}");
            return;
        }

        try
        {
            // Build a fresh PATH by reading the current registry value
            // so the launched process inherits the updated PATH
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
            };

            var freshPath = GetFreshPath();
            if (freshPath != null)
            {
                psi.Environment["PATH"] = freshPath;
            }

            Process.Start(psi);
            SetupLog.Write("[CompleteStep] LaunchButton_Click: cc-director launched");

            // Close the setup wizard
            Window.GetWindow(this)?.Close();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] LaunchButton_Click FAILED: {ex.Message}");
        }
    }

    private static string? GetFreshPath()
    {
        try
        {
            // Read user PATH from registry
            using var userKey = Registry.CurrentUser.OpenSubKey("Environment");
            var userPath = userKey?.GetValue("Path", "") as string ?? "";

            // Read system PATH from registry
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
}
