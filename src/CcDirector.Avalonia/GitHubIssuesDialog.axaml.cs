using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CcDirector.Avalonia;

public partial class GitHubIssuesDialog : Window
{
    private readonly string _url = string.Empty;

    public GitHubIssuesDialog()
    {
        InitializeComponent();
    }

    public GitHubIssuesDialog(string url)
    {
        InitializeComponent();
        _url = url;
        UrlTextBox.Text = url;
    }

    private async void CopyButton_Click(object? sender, RoutedEventArgs e)
    {
        await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(_url) ?? Task.CompletedTask);
        Close();
    }

    private void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        Close();
    }
}
