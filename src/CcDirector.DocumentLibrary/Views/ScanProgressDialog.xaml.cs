using System.Windows;
using CcDirector.Core.Utilities;
using CcDirector.DocumentLibrary.Services;

namespace CcDirector.DocumentLibrary.Views;

public partial class ScanProgressDialog : Window
{
    private readonly string _phase;
    private readonly string _libraryLabel;
    private CancellationTokenSource? _cts;

    private int _newCount;
    private int _updatedCount;
    private int _skippedCount;
    private int _errorCount;
    private int _summarizedCount;
    private int _dedupedCount;

    public ScanProgressDialog(string phase, string libraryLabel)
    {
        FileLog.Write($"[ScanProgressDialog] Constructor: phase={phase}, library={libraryLabel}");
        InitializeComponent();
        _phase = phase;
        _libraryLabel = libraryLabel;

        var phaseDisplay = phase == "scan" ? "Scanning" : "Summarizing";
        PhaseTitle.Text = $"{phaseDisplay}: {libraryLabel}";
        Title = $"{phaseDisplay} - {libraryLabel}";

        Loaded += ScanProgressDialog_Loaded;
    }

    private async void ScanProgressDialog_Loaded(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[ScanProgressDialog] Loaded: starting operation");
        _cts = new CancellationTokenSource();
        var client = new VaultCatalogClient();

        try
        {
            if (_phase == "scan")
            {
                await foreach (var evt in client.ScanLibraryAsync(_libraryLabel, _cts.Token))
                {
                    HandleEvent(evt);
                }
            }
            else
            {
                await foreach (var evt in client.SummarizeAsync(_libraryLabel, 50, _cts.Token))
                {
                    HandleEvent(evt);
                }
            }

            FileLog.Write("[ScanProgressDialog] Operation complete");
            BtnCancel.Content = "Close";
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[ScanProgressDialog] Operation cancelled");
            StatsText.Text = "Cancelled.";
            BtnCancel.Content = "Close";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ScanProgressDialog] Operation FAILED: {ex.Message}");
            StatsText.Text = $"Error: {ex.Message}";
            BtnCancel.Content = "Close";
        }
    }

    private void HandleEvent(Models.StreamEvent evt)
    {
        if (evt.Event == "progress")
        {
            // Update progress bar
            if (evt.Total > 0)
            {
                Progress.Maximum = evt.Total;
                Progress.Value = evt.Processed;
            }

            // Update current file
            CurrentFile.Text = evt.File ?? "";

            // Track counts
            switch (evt.Status)
            {
                case "new": _newCount++; break;
                case "updated": _updatedCount++; break;
                case "skipped": _skippedCount++; break;
                case "error": _errorCount++; break;
                case "summarized": _summarizedCount++; break;
                case "deduped": _dedupedCount++; break;
            }

            UpdateStats();
        }
        else if (evt.Event == "complete")
        {
            Progress.Value = Progress.Maximum;
            CurrentFile.Text = "Complete.";

            if (_phase == "scan")
                StatsText.Text = $"New: {evt.New}  |  Updated: {evt.Updated}  |  Skipped: {evt.Skipped}  |  Errors: {evt.Errors}";
            else
                StatsText.Text = $"Summarized: {evt.Summarized}  |  Deduped: {evt.Deduped}  |  Errors: {evt.Errors}";
        }
    }

    private void UpdateStats()
    {
        if (_phase == "scan")
            StatsText.Text = $"New: {_newCount}  |  Updated: {_updatedCount}  |  Skip: {_skippedCount}  |  Errors: {_errorCount}";
        else
            StatsText.Text = $"Summarized: {_summarizedCount}  |  Deduped: {_dedupedCount}  |  Errors: {_errorCount}";
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[ScanProgressDialog] BtnCancel_Click");
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        else
        {
            DialogResult = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        FileLog.Write("[ScanProgressDialog] OnClosed");
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }
}
