using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class CloneRepoDialog : Window
{
    private static readonly string LastDestFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "CcDirector", "last-clone-destination.txt");

    public string? RepoUrl { get; private set; }
    public string? Destination { get; private set; }

    public CloneRepoDialog()
    {
        FileLog.Write("[CloneRepoDialog] Constructor: initializing");
        InitializeComponent();

        // Restore last used destination, or fall back to ~/Repos
        var lastDest = LoadLastDestination();
        if (!string.IsNullOrWhiteSpace(lastDest) && Directory.Exists(lastDest))
            DestInput.Text = lastDest;
        else
        {
            var defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Repos");
            if (Directory.Exists(defaultDir))
                DestInput.Text = defaultDir;
        }

        Loaded += (_, _) => UrlInput.Focus();
        FileLog.Write("[CloneRepoDialog] Constructor: complete");
    }

    private void BtnBrowseGitHub_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[CloneRepoDialog] BtnBrowseGitHub_Click");
        // TODO: Open GitHubRepoPickerDialog once ported to Avalonia
        // var picker = new GitHubRepoPickerDialog();
        // picker.ShowDialog(this);
        // if (!string.IsNullOrEmpty(picker.SelectedUrl))
        //     UrlInput.Text = picker.SelectedUrl;
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[CloneRepoDialog] BtnBrowse_Click");

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Destination Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var folderPath = folders[0].Path.LocalPath;
            DestInput.Text = folderPath;
            FileLog.Write($"[CloneRepoDialog] BtnBrowse_Click: selected {folderPath}");
        }
    }

    private void BtnClone_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[CloneRepoDialog] BtnClone_Click");

        var url = UrlInput.Text?.Trim() ?? string.Empty;
        var dest = DestInput.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            // TODO: Show validation message to user
            FileLog.Write("[CloneRepoDialog] BtnClone_Click: empty URL");
            return;
        }

        if (string.IsNullOrWhiteSpace(dest))
        {
            // TODO: Show validation message to user
            FileLog.Write("[CloneRepoDialog] BtnClone_Click: empty destination");
            return;
        }

        // Derive repo name from URL for sub-folder
        var repoName = Path.GetFileNameWithoutExtension(
            url.TrimEnd('/').Split('/').LastOrDefault() ?? "repo");
        var fullDest = Path.Combine(dest, repoName);

        RepoUrl = url;
        Destination = fullDest;
        SaveLastDestination(dest);

        FileLog.Write($"[CloneRepoDialog] BtnClone_Click: url={url}, destination={fullDest}");
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[CloneRepoDialog] BtnCancel_Click");
        Close(false);
    }

    private static string? LoadLastDestination()
    {
        try { return File.Exists(LastDestFile) ? File.ReadAllText(LastDestFile).Trim() : null; }
        catch { return null; }
    }

    private static void SaveLastDestination(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(LastDestFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(LastDestFile, path);
        }
        catch { /* best effort */ }
    }
}
