using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Configuration;

public class RepositoryConfig : INotifyPropertyChanged
{
    private int _uncommittedCount;

    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime? LastUsed { get; set; }

    [JsonIgnore]
    public string LastUsedDisplay => LastUsed.HasValue
        ? FormatTimeAgo(LastUsed.Value)
        : string.Empty;

    [JsonIgnore]
    public int UncommittedCount
    {
        get => _uncommittedCount;
        set
        {
            if (_uncommittedCount == value) return;
            _uncommittedCount = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyLastUsedChanged()
    {
        OnPropertyChanged(nameof(LastUsed));
        OnPropertyChanged(nameof(LastUsedDisplay));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string FormatTimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt.ToUniversalTime();

        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }
}
