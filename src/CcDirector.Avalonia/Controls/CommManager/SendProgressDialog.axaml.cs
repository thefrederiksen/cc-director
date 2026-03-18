using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls.CommManager;

public partial class SendProgressDialog : Window
{
    public int TotalItems { get; private set; }

    public SendProgressDialog(int totalItems)
    {
        FileLog.Write($"[SendProgressDialog] Constructor: totalItems={totalItems}");
        InitializeComponent();
        TotalItems = totalItems;
        HeaderText.Text = $"Sending 0 of {totalItems}...";
        ProgressBar.Maximum = totalItems;
    }

    // Parameterless constructor for XAML designer
    public SendProgressDialog() : this(0) { }

    public void ReportProgress(int currentIndex, string itemDescription, int sent, int failed, int skipped)
    {
        FileLog.Write($"[SendProgressDialog] ReportProgress: {currentIndex}/{TotalItems} - {itemDescription}");
        HeaderText.Text = $"Sending {currentIndex} of {TotalItems}...";
        CurrentItemText.Text = itemDescription;
        ProgressBar.Value = currentIndex;
        SentText.Text = $"Sent: {sent}";
        FailedText.Text = $"Failed: {failed}";
        SkippedText.Text = $"Skipped: {skipped}";
    }

    public void ReportComplete(int sent, int failed, int skipped)
    {
        FileLog.Write($"[SendProgressDialog] ReportComplete: sent={sent}, failed={failed}, skipped={skipped}");
        HeaderText.Text = "Dispatch complete";
        CurrentItemText.Text = "";
        ProgressBar.Value = TotalItems;
        SentText.Text = $"Sent: {sent}";
        FailedText.Text = $"Failed: {failed}";
        SkippedText.Text = $"Skipped: {skipped}";
        CloseButton.IsVisible = true;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
