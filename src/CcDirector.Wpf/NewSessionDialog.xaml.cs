using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Configuration;
using Microsoft.Win32;

namespace CcDirector.Wpf;

public partial class NewSessionDialog : Window
{
    private readonly RepositoryRegistry? _registry;

    public string? SelectedPath { get; private set; }

    public NewSessionDialog(RepositoryRegistry? registry = null)
    {
        InitializeComponent();
        _registry = registry;

        if (_registry != null && _registry.Repositories.Count > 0)
        {
            RepoList.ItemsSource = _registry.Repositories.ToList();
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

            if (_registry != null)
            {
                _registry.TryAdd(dialog.FolderName);
                RepoList.ItemsSource = _registry.Repositories.ToList();
                RepoList.Visibility = Visibility.Visible;
            }
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
