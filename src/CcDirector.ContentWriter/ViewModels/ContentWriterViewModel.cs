using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CcDirector.ContentWriter.Models;
using CcDirector.ContentWriter.Services;
using CcDirector.Core.Utilities;

namespace CcDirector.ContentWriter.ViewModels;

public partial class ContentWriterViewModel : ObservableObject
{
    private readonly ContentStorageService _storage = new();
    private FileSystemWatcher? _watcher;
    private string? _currentFilePath;
    private List<DocumentListItem>? _pendingDocumentList;

    public ObservableCollection<DocumentListItem> InProgressDocuments { get; } = new();
    public ObservableCollection<DocumentListItem> CompletedDocuments { get; } = new();
    public ObservableCollection<SectionViewModel> Sections { get; } = new();

    [ObservableProperty]
    private DocumentListItem? _selectedDocument;

    [ObservableProperty]
    private string _documentTitle = "";

    [ObservableProperty]
    private string _statusText = "No document loaded";

    [ObservableProperty]
    private string _selectedSectionsText = "None";

    [ObservableProperty]
    private bool _isDocumentLoaded;

    public string? CurrentFilePath => _currentFilePath;

    /// <summary>
    /// Loads the document list from disk. Safe to call from a background thread.
    /// Call ApplyDocumentList() on the UI thread afterwards.
    /// </summary>
    public void LoadDocumentList()
    {
        FileLog.Write("[ContentWriterViewModel] LoadDocumentList");
        var docs = _storage.ListDocuments();
        _pendingDocumentList = docs.Select(doc => new DocumentListItem
        {
            FilePath = doc.FilePath,
            Name = doc.Name,
            Status = doc.Status,
            Modified = doc.Modified
        }).ToList();
        FileLog.Write($"[ContentWriterViewModel] LoadDocumentList: loaded {_pendingDocumentList.Count} documents");
    }

    /// <summary>
    /// Applies the loaded document list to the ObservableCollections. Must be called on the UI thread.
    /// </summary>
    public void ApplyDocumentList()
    {
        FileLog.Write("[ContentWriterViewModel] ApplyDocumentList");
        InProgressDocuments.Clear();
        CompletedDocuments.Clear();

        var items = _pendingDocumentList ?? [];
        _pendingDocumentList = null;

        foreach (var item in items)
        {
            if (item.Status == "completed")
                CompletedDocuments.Add(item);
            else
                InProgressDocuments.Add(item);
        }

        FileLog.Write($"[ContentWriterViewModel] ApplyDocumentList: {InProgressDocuments.Count} in progress, {CompletedDocuments.Count} completed");
    }

    public void RefreshDocumentList()
    {
        FileLog.Write("[ContentWriterViewModel] RefreshDocumentList");
        LoadDocumentList();
        ApplyDocumentList();
    }

    public void LoadDocument(string filePath)
    {
        FileLog.Write($"[ContentWriterViewModel] LoadDocument: {filePath}");
        StopWatching();

        var doc = _storage.LoadDocument(filePath);
        if (doc == null)
        {
            FileLog.Write("[ContentWriterViewModel] LoadDocument: document was null");
            return;
        }

        _currentFilePath = filePath;
        DocumentTitle = doc.Name;
        IsDocumentLoaded = true;

        Sections.Clear();
        foreach (var section in doc.Sections)
        {
            var vm = new SectionViewModel
            {
                Id = section.Id,
                Heading = section.Heading,
                Body = section.Body,
                IsSelected = doc.Selected.Contains(section.Id)
            };
            Sections.Add(vm);
        }

        UpdateSelectedText();
        StartWatching(filePath);
        StatusText = "Watching for changes...";
        FileLog.Write($"[ContentWriterViewModel] LoadDocument: loaded {doc.Sections.Count} sections");
    }

    public void CreateDocument(string name)
    {
        FileLog.Write($"[ContentWriterViewModel] CreateDocument: {name}");
        var doc = _storage.CreateNewDocument(name);
        RefreshDocumentList();

        var filePath = Path.Combine(_storage.StorageDirectory,
            string.Join("-", name.Split(Path.GetInvalidFileNameChars())).Replace(" ", "-").ToLowerInvariant() + ".json");

        // Find the actual file path from the refreshed list
        var match = InProgressDocuments.FirstOrDefault(d => d.Name == name);
        if (match != null)
            LoadDocument(match.FilePath);
    }

    public void ToggleSectionSelection(int sectionId)
    {
        FileLog.Write($"[ContentWriterViewModel] ToggleSectionSelection: {sectionId}");
        var section = Sections.FirstOrDefault(s => s.Id == sectionId);
        if (section == null) return;

        section.IsSelected = !section.IsSelected;
        UpdateSelectedText();
        PersistSelection();
    }

    public void SetSectionSelection(int sectionId, bool exclusive)
    {
        FileLog.Write($"[ContentWriterViewModel] SetSectionSelection: id={sectionId}, exclusive={exclusive}");
        if (exclusive)
        {
            foreach (var s in Sections)
                s.IsSelected = s.Id == sectionId;
        }
        else
        {
            var section = Sections.FirstOrDefault(s => s.Id == sectionId);
            if (section != null)
                section.IsSelected = !section.IsSelected;
        }

        UpdateSelectedText();
        PersistSelection();
    }

    public void ClearSelection()
    {
        FileLog.Write("[ContentWriterViewModel] ClearSelection");
        foreach (var s in Sections)
            s.IsSelected = false;
        UpdateSelectedText();
        PersistSelection();
    }

    public void MarkDocumentCompleted()
    {
        if (_currentFilePath == null) return;
        FileLog.Write($"[ContentWriterViewModel] MarkDocumentCompleted: {_currentFilePath}");

        var doc = _storage.LoadDocument(_currentFilePath);
        if (doc == null) return;

        doc.Status = "completed";
        _storage.SaveDocument(doc, _currentFilePath);
        RefreshDocumentList();
    }

    public void MarkDocumentInProgress()
    {
        if (_currentFilePath == null) return;
        FileLog.Write($"[ContentWriterViewModel] MarkDocumentInProgress: {_currentFilePath}");

        var doc = _storage.LoadDocument(_currentFilePath);
        if (doc == null) return;

        doc.Status = "in_progress";
        _storage.SaveDocument(doc, _currentFilePath);
        RefreshDocumentList();
    }

    private void PersistSelection()
    {
        if (_currentFilePath == null) return;

        var selectedIds = Sections.Where(s => s.IsSelected).Select(s => s.Id).ToList();
        _storage.UpdateSelection(_currentFilePath, selectedIds);
    }

    private void UpdateSelectedText()
    {
        var selected = Sections.Where(s => s.IsSelected).ToList();
        SelectedSectionsText = selected.Count == 0
            ? "None"
            : string.Join(", ", selected.Select(s => $"Section {s.Id}"));
    }

    private void StartWatching(string filePath)
    {
        FileLog.Write($"[ContentWriterViewModel] StartWatching: {filePath}");
        var dir = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);
        if (dir == null) return;

        _watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            FileLog.Write("[ContentWriterViewModel] StopWatching");
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private DateTime _lastReload = DateTime.MinValue;

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: ignore events within 500ms of each other
        var now = DateTime.Now;
        if ((now - _lastReload).TotalMilliseconds < 500) return;
        _lastReload = now;

        FileLog.Write($"[ContentWriterViewModel] OnFileChanged: {e.FullPath}");
        FileChanged?.Invoke(this, e.FullPath);
    }

    /// <summary>
    /// Raised when the watched file changes on disk (from Claude Code editing it).
    /// The view should handle this on the UI thread to reload content.
    /// </summary>
    public event EventHandler<string>? FileChanged;

    public void Dispose()
    {
        FileLog.Write("[ContentWriterViewModel] Dispose");
        StopWatching();
    }
}

public partial class SectionViewModel : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _heading = "";

    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    private bool _isSelected;
}

public class DocumentListItem
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime Modified { get; set; }
}
