using System.Windows;
using System.Windows.Media;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class LoadWorkspaceDialog : Window
{
    private readonly WorkspaceStore _store;
    private List<WorkspaceListItem> _workspaces = new();

    public WorkspaceDefinition? SelectedWorkspace { get; private set; }

    public LoadWorkspaceDialog(WorkspaceStore store, bool startupMode = false)
    {
        FileLog.Write($"[LoadWorkspaceDialog] Constructor: startupMode={startupMode}");
        InitializeComponent();

        _store = store;

        if (startupMode)
        {
            Title = "Select Workspace";
            BtnCancel.Content = "Skip";
        }

        Loaded += (_, _) => LoadWorkspaces();
    }

    private void LoadWorkspaces()
    {
        FileLog.Write("[LoadWorkspaceDialog] LoadWorkspaces");

        var definitions = _store.LoadAll();
        _workspaces = definitions.Select(d => new WorkspaceListItem(d)).ToList();

        if (_workspaces.Count == 0)
        {
            WorkspaceListBox.Visibility = Visibility.Collapsed;
            TxtEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            WorkspaceListBox.ItemsSource = _workspaces;
        }
    }

    private void WorkspaceListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
        {
            BtnLoad.IsEnabled = false;
            BtnDelete.IsEnabled = false;
            TxtPreviewEmpty.Visibility = Visibility.Visible;
            PreviewList.Visibility = Visibility.Collapsed;
            TxtPreviewDescription.Visibility = Visibility.Collapsed;
            return;
        }

        BtnLoad.IsEnabled = true;
        BtnDelete.IsEnabled = true;

        if (!string.IsNullOrWhiteSpace(item.Definition.Description))
        {
            TxtPreviewDescription.Text = item.Definition.Description;
            TxtPreviewDescription.Visibility = Visibility.Visible;
        }
        else
        {
            TxtPreviewDescription.Visibility = Visibility.Collapsed;
        }

        var previewItems = item.Definition.Sessions
            .OrderBy(s => s.SortOrder)
            .Select(s => new PreviewSessionItem
            {
                DisplayName = !string.IsNullOrWhiteSpace(s.CustomName)
                    ? s.CustomName
                    : System.IO.Path.GetFileName(s.RepoPath.TrimEnd('\\', '/')),
                RepoPath = s.RepoPath,
                HasColor = !string.IsNullOrWhiteSpace(s.CustomColor),
                ColorBrush = GetColorBrush(s.CustomColor)
            }).ToList();

        PreviewList.ItemsSource = previewItems;
        TxtPreviewEmpty.Visibility = Visibility.Collapsed;
        PreviewList.Visibility = Visibility.Visible;
    }

    private void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
            return;

        FileLog.Write($"[LoadWorkspaceDialog] BtnLoad_Click: name={item.Definition.Name}");
        SelectedWorkspace = item.Definition;
        DialogResult = true;
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
            return;

        var result = MessageBox.Show(this,
            $"Delete workspace \"{item.Definition.Name}\"?",
            "Delete Workspace", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        var slug = WorkspaceStore.ToSlug(item.Definition.Name);
        FileLog.Write($"[LoadWorkspaceDialog] BtnDelete_Click: deleting slug={slug}");
        _store.Delete(slug);

        LoadWorkspaces();
        BtnLoad.IsEnabled = false;
        BtnDelete.IsEnabled = false;
        TxtPreviewEmpty.Visibility = Visibility.Visible;
        PreviewList.Visibility = Visibility.Collapsed;
        TxtPreviewDescription.Visibility = Visibility.Collapsed;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[LoadWorkspaceDialog] BtnCancel_Click");
        DialogResult = false;
    }

    private static SolidColorBrush GetColorBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return new SolidColorBrush(Colors.Transparent);

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return new SolidColorBrush(Colors.Transparent);
        }
    }

    internal class WorkspaceListItem
    {
        public WorkspaceDefinition Definition { get; }
        public string Name => Definition.Name;
        public string SessionCountDisplay => $"{Definition.Sessions.Count} session{(Definition.Sessions.Count == 1 ? "" : "s")}";
        public string UpdatedDisplay => Definition.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

        public WorkspaceListItem(WorkspaceDefinition definition)
        {
            Definition = definition;
        }
    }

    internal class PreviewSessionItem
    {
        public string DisplayName { get; set; } = string.Empty;
        public string RepoPath { get; set; } = string.Empty;
        public bool HasColor { get; set; }
        public SolidColorBrush ColorBrush { get; set; } = new(Colors.Transparent);
    }
}
