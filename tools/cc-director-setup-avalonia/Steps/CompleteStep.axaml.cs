using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class CompleteStep : UserControl
{
    private readonly string _installPath;

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
}
