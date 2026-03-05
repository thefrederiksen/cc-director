using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CcDirector.Core.Utilities;
using CcDirector.DocumentLibrary.Models;
using CcDirector.DocumentLibrary.Services;

namespace CcDirector.DocumentLibrary.ViewModels;

public class DocumentLibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CatalogDatabase _db = new();
    private Library? _selectedLibrary;
    private string? _selectedRelativeDir;
    private CatalogEntry? _selectedEntry;
    private CatalogEntry? _selectedSearchEntry;
    private string _searchQuery = string.Empty;
    private string? _activeExtFilter;
    private string _statusText = string.Empty;
    private bool _isLoading;
    private string _sortColumn = "file_name";
    private bool _sortAscending = true;
    private int _totalEntryCount;
    private CancellationTokenSource? _searchDebounce;

    public ObservableCollection<Library> Libraries { get; } = [];
    public ObservableCollection<CatalogEntry> Entries { get; } = [];
    public ObservableCollection<CatalogEntry> SearchResults { get; } = [];

    public Library? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (_selectedLibrary == value) return;
            _selectedLibrary = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Relative directory path for folder filtering (e.g. "Software/SubDir").</summary>
    public string? SelectedRelativeDir
    {
        get => _selectedRelativeDir;
        set
        {
            _selectedRelativeDir = value;
            OnPropertyChanged();
        }
    }

    public CatalogEntry? SelectedEntry
    {
        get => _selectedEntry;
        set { _selectedEntry = value; OnPropertyChanged(); }
    }

    public CatalogEntry? SelectedSearchEntry
    {
        get => _selectedSearchEntry;
        set { _selectedSearchEntry = value; OnPropertyChanged(); }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value) return;
            _searchQuery = value;
            OnPropertyChanged();
            DebouncedSearch();
        }
    }

    public string? ActiveExtFilter
    {
        get => _activeExtFilter;
        set
        {
            if (_activeExtFilter == value) return;
            _activeExtFilter = value;
            OnPropertyChanged();
            if (_selectedLibrary is not null)
                _ = ReloadCurrentViewAsync();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public string SortColumn
    {
        get => _sortColumn;
        set
        {
            if (_sortColumn == value)
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _sortColumn = value;
                _sortAscending = true;
            }
            OnPropertyChanged();
            _ = ReloadCurrentViewAsync();
        }
    }

    public bool SortAscending => _sortAscending;

    public async Task InitializeAsync()
    {
        FileLog.Write("[DocumentLibraryViewModel] InitializeAsync");
        try
        {
            var libs = await Task.Run(() => _db.ListLibraries());
            Application.Current.Dispatcher.Invoke(() =>
            {
                Libraries.Clear();
                foreach (var lib in libs)
                    Libraries.Add(lib);
            });

            StatusText = $"{libs.Count} libraries registered";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DocumentLibraryViewModel] InitializeAsync FAILED: {ex.Message}");
            StatusText = $"Error loading libraries: {ex.Message}";
        }
    }

    /// <summary>Get relative directory paths for building a multi-level tree.</summary>
    public List<string> GetRelativeDirectories(int libraryId, string libraryPath)
    {
        FileLog.Write($"[DocumentLibraryViewModel] GetRelativeDirectories: libraryId={libraryId}");
        return _db.GetRelativeDirectories(libraryId, libraryPath);
    }

    /// <summary>Reload the current view (after filter/sort change).</summary>
    public async Task ReloadCurrentViewAsync()
    {
        if (_selectedLibrary is null) return;

        if (!string.IsNullOrEmpty(_selectedRelativeDir))
        {
            await LoadEntriesByDirectoryAsync(_selectedLibrary.Id, _selectedLibrary.Path, _selectedRelativeDir);
        }
        else
        {
            await LoadEntriesAsync();
        }
    }

    /// <summary>Load entries filtered by relative directory path.</summary>
    public async Task LoadEntriesByDirectoryAsync(int libraryId, string libraryPath, string relativeDir)
    {
        FileLog.Write($"[DocumentLibraryViewModel] LoadEntriesByDirectoryAsync: lib={libraryId}, dir={relativeDir}");
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() =>
                _db.ListEntriesByDirectory(
                    libraryId, libraryPath, relativeDir,
                    ext: _activeExtFilter,
                    sortColumn: _sortColumn,
                    sortAscending: _sortAscending,
                    limit: 500));

            Application.Current.Dispatcher.Invoke(() =>
            {
                Entries.Clear();
                foreach (var e in entries)
                    Entries.Add(e);
            });

            _totalEntryCount = entries.Count;
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DocumentLibraryViewModel] LoadEntriesByDirectoryAsync FAILED: {ex.Message}");
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadEntriesAsync()
    {
        FileLog.Write($"[DocumentLibraryViewModel] LoadEntriesAsync: lib={_selectedLibrary?.Label}, ext={_activeExtFilter}, sort={_sortColumn}");
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() =>
                _db.ListEntries(
                    libraryId: _selectedLibrary?.Id,
                    ext: _activeExtFilter,
                    sortColumn: _sortColumn,
                    sortAscending: _sortAscending,
                    limit: 500));

            _totalEntryCount = await Task.Run(() =>
                _db.GetEntryCount(_selectedLibrary?.Id, ext: _activeExtFilter));

            Application.Current.Dispatcher.Invoke(() =>
            {
                Entries.Clear();
                foreach (var e in entries)
                    Entries.Add(e);
            });

            UpdateStatusText();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DocumentLibraryViewModel] LoadEntriesAsync FAILED: {ex.Message}");
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SearchEntriesAsync(string query)
    {
        FileLog.Write($"[DocumentLibraryViewModel] SearchEntriesAsync: query={query}, lib={_selectedLibrary?.Label}");
        IsLoading = true;
        try
        {
            var results = await Task.Run(() => _db.Search(
                query,
                libraryId: _selectedLibrary?.Id,
                ext: _activeExtFilter));

            Application.Current.Dispatcher.Invoke(() =>
            {
                Entries.Clear();
                foreach (var e in results)
                    Entries.Add(e);
            });

            StatusText = $"{results.Count} results for \"{query}\"";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DocumentLibraryViewModel] SearchEntriesAsync FAILED: {ex.Message}");
            StatusText = $"Search error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Search globally across all libraries (for Search tab).</summary>
    public async Task SearchGlobalAsync(string query)
    {
        FileLog.Write($"[DocumentLibraryViewModel] SearchGlobalAsync: query={query}");
        IsLoading = true;
        try
        {
            var results = await Task.Run(() => _db.Search(
                query,
                libraryId: null,
                ext: _activeExtFilter));

            Application.Current.Dispatcher.Invoke(() =>
            {
                SearchResults.Clear();
                foreach (var e in results)
                    SearchResults.Add(e);
            });

            StatusText = $"{results.Count} results for \"{query}\"";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DocumentLibraryViewModel] SearchGlobalAsync FAILED: {ex.Message}");
            StatusText = $"Search error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RefreshLibrariesAsync()
    {
        FileLog.Write("[DocumentLibraryViewModel] RefreshLibrariesAsync");
        await InitializeAsync();
    }

    public void OpenFile(CatalogEntry entry)
    {
        FileLog.Write($"[DocumentLibraryViewModel] OpenFile: {entry.FilePath}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = entry.FilePath,
            UseShellExecute = true,
        });
    }

    private void DebouncedSearch()
    {
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        var token = _searchDebounce.Token;

        _ = Task.Delay(300, token).ContinueWith(async _ =>
        {
            if (token.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(_searchQuery))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SearchResults.Clear();
                });
                StatusText = "";
            }
            else
            {
                await SearchGlobalAsync(_searchQuery);
            }
        }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void UpdateStatusText()
    {
        var showing = Entries.Count;
        var summarized = Entries.Count(e => e.Status == "summarized");
        var pending = Entries.Count(e => e.Status == "pending");
        var totalStr = _totalEntryCount > showing ? $"{showing} of {_totalEntryCount}" : $"{showing}";
        StatusText = $"{totalStr} files | {summarized} summarized | {pending} pending";
    }

    public void Dispose()
    {
        FileLog.Write("[DocumentLibraryViewModel] Dispose");
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
