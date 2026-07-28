using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class InstallStep : UserControl
{
    private ToolDownloadItem? _directorItem;

    public InstallStep()
    {
        InitializeComponent();
        LogFooter.Text = $"Setup log: {SetupLog.Path}";
        SetupLog.Write("[InstallStep] Created");
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] OpenLogButton_Click");
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SetupLog.Write($"[InstallStep] OpenLogButton_Click FAILED: {ex.Message}"); }
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] ReportButton_Click");
        try
        {
            IssueReporter.Open(IssueReporter.BuildUrl("[install] Setup stuck or failing on Windows", BuildIssueBody()));
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[InstallStep] ReportButton_Click FAILED: {ex.Message}");
            MessageBox.Show(
                $"Could not open the browser. Please file an issue at {IssueReporter.NewIssueBase} and attach the log:\n{SetupLog.Path}",
                "Report a problem", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private string BuildIssueBody()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## What happened");
        sb.AppendLine("<!-- e.g. the installer was stuck on a step, or a component failed. -->");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"- Arch: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"- Status when reported: {StatusText.Text}");
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
            var all = System.IO.File.ReadAllLines(SetupLog.Path);
            var start = Math.Max(0, all.Length - lines);
            return string.Join("\n", all[start..]);
        }
        catch (Exception ex)
        {
            return $"(could not read log: {ex.Message})";
        }
    }

    public void SetItems(List<ToolDownloadItem> items)
    {
        _directorItem = items.FirstOrDefault(i => i.Name == "cc-director");

        // The cc-* tools are no longer installed here (the app provisions them on first launch), so the
        // Tools card is a static note - there are no tool rows to bind or track. Skills are not placed
        // on the machine at all any more (issue 995): they are held on the Gateway and fetched, so this
        // step has nothing to say about them.

        // Bind director item changes
        if (_directorItem != null)
        {
            _directorItem.PropertyChanged += (_, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (e.PropertyName == nameof(ToolDownloadItem.Status))
                    {
                        DirectorStatus.Text = _directorItem.Status;
                        DirectorStatus.Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(_directorItem.StatusColor));
                    }
                    else if (e.PropertyName == nameof(ToolDownloadItem.Progress))
                    {
                        DirectorProgress.Value = _directorItem.Progress;
                    }
                    else if (e.PropertyName == nameof(ToolDownloadItem.SizeText))
                    {
                        DirectorSize.Text = _directorItem.SizeText;
                    }
                });
            };
        }
    }

    public event Action? OnRepairRequested;

    public void SetUpdateMode()
    {
        HeadingText.Text = "Updating";
    }

    public void SetUpToDate(string version)
    {
        SetupLog.Write($"[InstallStep] SetUpToDate: version={version}");

        HeadingText.Text = "Up to Date";
        StatusText.Text = $"You are running the latest version ({version}).";
        RepairButton.Visibility = Visibility.Visible;

        var upToDateColor = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#22C55E"));

        DirectorStatus.Text = "Up to date";
        DirectorStatus.Foreground = upToDateColor;
    }

    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] RepairButton_Click");
        RepairButton.Visibility = Visibility.Collapsed;
        OnRepairRequested?.Invoke();
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }

    /// <summary>Reveal the Gateway and Cockpit card (Gateway-role installs only).</summary>
    public void ShowGatewaySection()
    {
        GatewaySection.Visibility = Visibility.Visible;
    }

    /// <summary>The Gateway tray app + Cockpit are being placed (indeterminate - the CLI streams log lines).</summary>
    public void SetGatewayInstalling()
    {
        GatewayStatus.Text = "Installing";
        GatewayStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC"));
        GatewayProgress.Visibility = Visibility.Visible;
    }

    public void SetGatewayDone()
    {
        GatewayStatus.Text = "Done";
        GatewayStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"));
        GatewayProgress.Visibility = Visibility.Collapsed;
    }

    public void SetGatewayFailed()
    {
        GatewayStatus.Text = "Failed";
        GatewayStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CC4444"));
        GatewayProgress.Visibility = Visibility.Collapsed;
    }

    public void ShowProgress()
    {
        DirectorProgress.Visibility = Visibility.Visible;
    }
}
