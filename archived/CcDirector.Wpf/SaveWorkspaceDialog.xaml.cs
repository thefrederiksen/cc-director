using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class SaveWorkspaceDialog : Window
{
    private readonly WorkspaceStore _store;
    private readonly List<SaveSessionItem> _items;

    public WorkspaceDefinition? Result { get; private set; }

    public SaveWorkspaceDialog(WorkspaceStore store, IEnumerable<SessionViewModel> sessions)
    {
        FileLog.Write("[SaveWorkspaceDialog] Constructor");
        InitializeComponent();

        _store = store;
        _items = sessions.Select((vm, i) => new SaveSessionItem
        {
            IsSelected = true,
            DisplayName = vm.DisplayName,
            RepoPath = vm.Session.RepoPath,
            CustomName = vm.Session.CustomName,
            CustomColor = vm.Session.CustomColor,
            ClaudeArgs = vm.Session.ClaudeArgs,
            SortOrder = i,
            HasColor = !string.IsNullOrWhiteSpace(vm.Session.CustomColor),
            ColorBrush = GetColorBrush(vm.Session.CustomColor)
        }).ToList();

        SessionListBox.ItemsSource = _items;
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

    private void TxtName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        BtnSave.IsEnabled = !string.IsNullOrWhiteSpace(name);

        if (!string.IsNullOrWhiteSpace(name))
        {
            var slug = WorkspaceStore.ToSlug(name);
            if (_store.Exists(slug))
            {
                TxtWarning.Text = $"A workspace with this name already exists and will be overwritten.";
                TxtWarning.Visibility = Visibility.Visible;
            }
            else
            {
                TxtWarning.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            TxtWarning.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        FileLog.Write($"[SaveWorkspaceDialog] BtnSave_Click: name={name}");

        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Please select at least one session.", "No Sessions Selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var slug = WorkspaceStore.ToSlug(name);
        var existing = _store.Load(slug);

        Result = new WorkspaceDefinition
        {
            Version = 1,
            Name = name,
            Description = string.IsNullOrWhiteSpace(TxtDescription.Text) ? null : TxtDescription.Text.Trim(),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Sessions = selected.Select(s => new WorkspaceSessionEntry
            {
                RepoPath = s.RepoPath,
                CustomName = s.CustomName,
                CustomColor = s.CustomColor,
                SortOrder = s.SortOrder,
                ClaudeArgs = s.ClaudeArgs
            }).ToList()
        };

        _store.Save(Result);
        FileLog.Write($"[SaveWorkspaceDialog] Workspace saved: {name} ({selected.Count} sessions)");

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[SaveWorkspaceDialog] BtnCancel_Click");
        DialogResult = false;
    }

    internal class SaveSessionItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string DisplayName { get; set; } = string.Empty;
        public string RepoPath { get; set; } = string.Empty;
        public string? CustomName { get; set; }
        public string? CustomColor { get; set; }
        public string? ClaudeArgs { get; set; }
        public int SortOrder { get; set; }
        public bool HasColor { get; set; }
        public SolidColorBrush ColorBrush { get; set; } = new(Colors.Transparent);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
