using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CcDirector.Core.Utilities;
using CcDirector.DocumentLibrary.Models;
using CcDirector.DocumentLibrary.Services;

namespace CcDirector.DocumentLibrary.ViewModels;

public class DocumentLibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly VaultCatalogClient _client = new();
    private Library? _selectedLibrary;
    private CatalogEntry? _selectedEntry;
    private string _searchQuery = string.Empty;
    private string? _activeExtFilter;
    private string _statusText = string.Empty;
    private bool _isLoading;
    private CancellationTokenSource? _searchDebounce;

    public ObservableCollection<Library> Libraries { get; } = [];
    public ObservableCollection<CatalogEntry> Entries { get; } = [];

    public Library? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (_selectedLibrary == value) return;
            _selectedLibrary = value;
            OnPropertyChanged();
            _ = LoadEntriesAsync();
        }
    }

    public CatalogEntry? SelectedEntry
    {
        get => _selectedEntry;
        set { _selectedEntry = value; OnPropertyChanged(); }
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
            _ = LoadEntriesAsync();
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

    public async Task InitializeAsync()
    {
        FileLog.Write("[DocumentLibraryViewModel] InitializeAsync");
        IsLoading = true;
        StatusText = "Loading libraries...";
        try
        {
            var libs = await Task.Run(() => _client.ListLibrariesAsync());
            Libraries.Clear();
            foreach (var lib in libs)
                Libraries.Add(lib);

            StatusText = $"{Libraries.Count} libraries registered";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DocumentLibraryViewModel] InitializeAsync FAILED: {ex.Message}");
            StatusText = $"Error loading libraries: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadEntriesAsync()
    {
        FileLog.Write($"[DocumentLibraryViewModel] LoadEntriesAsync: library={_selectedLibrary?.Label}, ext={_activeExtFilter}");
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() =>
                _client.ListEntriesAsync(_selectedLibrary?.Label, _activeExtFilter));

            Entries.Clear();
            foreach (var e in entries)
                Entries.Add(e);

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
        FileLog.Write($"[DocumentLibraryViewModel] SearchEntriesAsync: {query}");
        IsLoading = true;
        try
        {
            var results = await Task.Run(() => _client.SearchAsync(query));

            Entries.Clear();
            foreach (var e in results)
                Entries.Add(e);

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

    public async Task RefreshLibrariesAsync()
    {
        FileLog.Write("[DocumentLibraryViewModel] RefreshLibrariesAsync");
        await InitializeAsync();
    }

    public void OpenFile(CatalogEntry entry)
    {
        FileLog.Write($"[DocumentLibraryViewModel] OpenFile: {entry.FilePath}");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.FilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DocumentLibraryViewModel] OpenFile FAILED: {ex.Message}");
        }
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
                await LoadEntriesAsync();
            else
                await SearchEntriesAsync(_searchQuery);
        }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void UpdateStatusText()
    {
        var total = Entries.Count;
        var summarized = Entries.Count(e => e.Status == "summarized");
        var pending = Entries.Count(e => e.Status == "pending");
        StatusText = $"{total} files | {summarized} summarized | {pending} pending";
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
