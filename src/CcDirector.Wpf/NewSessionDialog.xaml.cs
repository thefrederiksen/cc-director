using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Configuration;
using Microsoft.Win32;

namespace CcDirector.Wpf;

public partial class NewSessionDialog : Window
{
    public string? SelectedPath { get; private set; }

    public NewSessionDialog(List<RepositoryConfig>? repos = null)
    {
        InitializeComponent();

        if (repos != null && repos.Count > 0)
        {
            RepoList.ItemsSource = repos;
            RepoList.Visibility = Visibility.Visible;
        }
        else
        {
            RepoList.Visibility = Visibility.Collapsed;
        }
    }

    private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is RepositoryConfig repo)
        {
            PathInput.Text = repo.Path;
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Repository Folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathInput.Text = dialog.FolderName;
        }
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = PathInput.Text;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
