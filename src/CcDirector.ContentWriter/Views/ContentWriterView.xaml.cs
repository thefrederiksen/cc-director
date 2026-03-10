using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CcDirector.ContentWriter.ViewModels;
using CcDirector.Core.Utilities;

namespace CcDirector.ContentWriter.Views;

public partial class ContentWriterView : UserControl, IDisposable
{
    public ContentWriterViewModel ViewModel { get; }

    /// <summary>
    /// Raised when the user selects a session name from the dropdown.
    /// The MainWindow should handle this to embed the terminal.
    /// </summary>
    public event EventHandler<string>? SessionLinkRequested;

    public ContentWriterView()
    {
        InitializeComponent();

        ViewModel = new ContentWriterViewModel();
        DataContext = ViewModel;

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ContentWriterViewModel.DocumentTitle))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    TxtDocumentTitle.Text = ViewModel.DocumentTitle;
                    PreviewPlaceholder.Visibility = ViewModel.IsDocumentLoaded
                        ? Visibility.Collapsed : Visibility.Visible;
                });
            }
            else if (e.PropertyName == nameof(ContentWriterViewModel.StatusText))
            {
                Dispatcher.BeginInvoke(() => TxtStatus.Text = ViewModel.StatusText);
            }
            else if (e.PropertyName == nameof(ContentWriterViewModel.SelectedSectionsText))
            {
                Dispatcher.BeginInvoke(() => TxtSelectedSections.Text = ViewModel.SelectedSectionsText);
            }
            else if (e.PropertyName == nameof(ContentWriterViewModel.IsDocumentLoaded))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    PreviewPlaceholder.Visibility = ViewModel.IsDocumentLoaded
                        ? Visibility.Collapsed : Visibility.Visible;
                    BtnAddSection.IsEnabled = ViewModel.IsDocumentLoaded;
                    BtnMarkComplete.IsEnabled = ViewModel.IsDocumentLoaded;
                });
            }
        };

        ViewModel.FileChanged += OnFileChangedExternally;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        InProgressList.ItemsSource = ViewModel.InProgressDocuments;
        CompletedList.ItemsSource = ViewModel.CompletedDocuments;
        await Task.Run(() => ViewModel.LoadDocumentList());
        ViewModel.ApplyDocumentList();
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnFileChangedExternally(object? sender, string filePath)
    {
        FileLog.Write($"[ContentWriterView] OnFileChangedExternally: {filePath}");
        Dispatcher.BeginInvoke(() =>
        {
            ViewModel.LoadDocument(filePath);
            TxtFilePath.Text = filePath;
        });
    }

    private void BtnNewDocument_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[ContentWriterView] BtnNewDocument_Click");
        var dialog = new NewDocumentDialog
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.DocumentName))
        {
            ViewModel.CreateDocument(dialog.DocumentName);
            InProgressList.ItemsSource = ViewModel.InProgressDocuments;
            CompletedList.ItemsSource = ViewModel.CompletedDocuments;
        }
    }

    private void DocumentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InProgressList.SelectedItem is not DocumentListItem item) return;

        FileLog.Write($"[ContentWriterView] DocumentList_SelectionChanged: {item.Name}");
        CompletedList.SelectedIndex = -1;
        ViewModel.LoadDocument(item.FilePath);
        TxtFilePath.Text = item.FilePath;
    }

    private void CompletedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompletedList.SelectedItem is not DocumentListItem item) return;

        FileLog.Write($"[ContentWriterView] CompletedList_SelectionChanged: {item.Name}");
        InProgressList.SelectedIndex = -1;
        ViewModel.LoadDocument(item.FilePath);
        TxtFilePath.Text = item.FilePath;
    }

    private void SectionCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SectionViewModel section) return;

        FileLog.Write($"[ContentWriterView] SectionCard_MouseLeftButtonDown: section={section.Id}");

        bool exclusive = !Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl);
        ViewModel.SetSectionSelection(section.Id, exclusive);
    }

    private void BtnAddSection_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDocumentLoaded || ViewModel.CurrentFilePath == null) return;
        FileLog.Write("[ContentWriterView] BtnAddSection_Click");

        var nextId = ViewModel.Sections.Count > 0
            ? ViewModel.Sections.Max(s => s.Id) + 1
            : 1;

        ViewModel.Sections.Add(new SectionViewModel
        {
            Id = nextId,
            Heading = $"Section {nextId}",
            Body = ""
        });

        // Persist the new section to disk
        PersistCurrentDocument();
    }

    private void BtnMarkComplete_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDocumentLoaded) return;
        FileLog.Write("[ContentWriterView] BtnMarkComplete_Click");

        var doc = new Services.ContentStorageService().LoadDocument(ViewModel.CurrentFilePath!);
        if (doc == null) return;

        if (doc.Status == "completed")
            ViewModel.MarkDocumentInProgress();
        else
            ViewModel.MarkDocumentCompleted();

        InProgressList.ItemsSource = ViewModel.InProgressDocuments;
        CompletedList.ItemsSource = ViewModel.CompletedDocuments;
    }

    private void SessionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionComboBox.SelectedItem is not string sessionName) return;

        FileLog.Write($"[ContentWriterView] SessionComboBox_SelectionChanged: {sessionName}");
        SessionLinkRequested?.Invoke(this, sessionName);
    }

    /// <summary>
    /// Called by MainWindow to populate the session dropdown.
    /// </summary>
    public void UpdateSessionList(IEnumerable<string> sessionNames)
    {
        FileLog.Write("[ContentWriterView] UpdateSessionList");
        var current = SessionComboBox.SelectedItem as string;
        SessionComboBox.Items.Clear();
        SessionComboBox.Items.Add("(none)");
        foreach (var name in sessionNames)
            SessionComboBox.Items.Add(name);

        if (current != null && SessionComboBox.Items.Contains(current))
            SessionComboBox.SelectedItem = current;
        else
            SessionComboBox.SelectedIndex = 0;
    }

    /// <summary>
    /// The panel where the MainWindow can host an embedded terminal.
    /// </summary>
    public Panel TerminalHost => TerminalHostPanel;

    /// <summary>
    /// Hide/show the terminal placeholder text.
    /// </summary>
    public void SetTerminalLinked(bool linked)
    {
        TerminalPlaceholder.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PersistCurrentDocument()
    {
        if (ViewModel.CurrentFilePath == null) return;

        var storage = new Services.ContentStorageService();
        var doc = storage.LoadDocument(ViewModel.CurrentFilePath);
        if (doc == null) return;

        doc.Sections = ViewModel.Sections.Select(s => new Models.ContentSection
        {
            Id = s.Id,
            Heading = s.Heading,
            Body = s.Body
        }).ToList();

        storage.SaveDocument(doc, ViewModel.CurrentFilePath);
    }

    public void Dispose()
    {
        FileLog.Write("[ContentWriterView] Dispose");
        ViewModel.Dispose();
    }
}
