using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class InstallStep : UserControl
{
    private ToolDownloadItem? _directorItem;
    private ToolDownloadItem? _launcherItem;

    public InstallStep()
    {
        InitializeComponent();
        LogFooter.Text = $"Setup log: {SetupLog.Path}";
        SetupLog.Write("[InstallStep] Created");
    }

    private void OpenLogButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] OpenLogButton_Click");
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
        catch (Exception ex) { SetupLog.Write($"[InstallStep] OpenLogButton_Click FAILED: {ex.Message}"); }
    }

    private void ReportButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] ReportButton_Click");
        try
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Windows";
            IssueReporter.Open(IssueReporter.BuildUrl($"[install] Setup stuck or failing on {os}", BuildIssueBody(os)));
        }
        catch (Exception ex) { SetupLog.Write($"[InstallStep] ReportButton_Click FAILED: {ex.Message}"); }
    }

    private string BuildIssueBody(string os)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## What happened");
        sb.AppendLine("<!-- e.g. the installer was stuck on a step, or a component failed. -->");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- OS: {os} ({RuntimeInformation.OSDescription})");
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
            var all = File.ReadAllLines(SetupLog.Path);
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
        _launcherItem = items.FirstOrDefault(i => i.Name == "cc-launcher");

        // The cc-* tools are no longer installed here (the app provisions them on first launch), so the
        // Tools card is a static note - there are no tool rows to bind or track. Skills are not placed
        // on the machine at all any more (issue 995): they are held on the Gateway and fetched, so this
        // step has nothing to say about them.

        BindItem(_directorItem, DirectorStatus, DirectorProgress, DirectorSize, DirectorDetail);
        BindItem(_launcherItem, LauncherStatus, LauncherProgress, LauncherSize, LauncherDetail);
    }

    /// <summary>
    /// Mirror one component's live state onto its card, including the failure REASON.
    ///
    /// The reason was always computed - every failure path in EngineInstallRunner sets StatusDetail -
    /// and then discarded, because nothing bound it. A user saw the word "Failed" and had to open a
    /// log to learn that, for example, the launcher was healthy but had not registered its launch
    /// agent property list.
    /// </summary>
    private static void BindItem(ToolDownloadItem? item, TextBlock status, ProgressBar progress, TextBlock size, TextBlock detail)
    {
        if (item is null) return;
        item.PropertyChanged += (_, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (e.PropertyName == nameof(ToolDownloadItem.Status))
                {
                    status.Text = item.Status;
                    status.Foreground = SolidColorBrush.Parse(item.StatusColor);
                }
                else if (e.PropertyName == nameof(ToolDownloadItem.Progress))
                {
                    progress.Value = item.Progress;
                    if (item.Progress > 0) progress.IsVisible = true;
                }
                else if (e.PropertyName == nameof(ToolDownloadItem.SizeText))
                {
                    size.Text = item.SizeText;
                }
                else if (e.PropertyName == nameof(ToolDownloadItem.StatusDetail))
                {
                    detail.Text = item.StatusDetail;
                    detail.IsVisible = !string.IsNullOrWhiteSpace(item.StatusDetail);
                }
            });
        };
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
        RepairButton.IsVisible = true;

        var upToDateBrush = SolidColorBrush.Parse("#22C55E");

        DirectorStatus.Text = "Up to date";
        DirectorStatus.Foreground = upToDateBrush;

        // The launcher is part of this install too, so it gets the same verdict. Leaving it at
        // "Pending" on a machine that is already current made the card read as unfinished work.
        LauncherStatus.Text = "Up to date";
        LauncherStatus.Foreground = upToDateBrush;
    }

    private void RepairButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] RepairButton_Click");
        RepairButton.IsVisible = false;
        OnRepairRequested?.Invoke();
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }

    public void ShowProgress()
    {
        DirectorProgress.IsVisible = true;
    }
}
