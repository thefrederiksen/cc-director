namespace CcDirector.Wpf.Controls;

/// <summary>
/// Common interface for all file viewer controls used in document tabs.
/// </summary>
public interface IFileViewer
{
    string? FilePath { get; }
    bool IsDirty { get; }
    string GetDisplayName();
    Task LoadFileAsync(string filePath);
    Task SaveAsync();
    void ShowLoadError(string message);
}
