using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Sessions;

namespace CcDirector.Avalonia;

public class SessionViewModel : INotifyPropertyChanged
{
    private static readonly Dictionary<ActivityState, ISolidColorBrush> ActivityBrushes = new()
    {
        { ActivityState.Starting, new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) },
        { ActivityState.Idle, new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)) },
        { ActivityState.Working, new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) },
        { ActivityState.WaitingForInput, new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)) },
        { ActivityState.WaitingForPerm, new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)) },
        { ActivityState.Exited, new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)) },
    };

    private static readonly Dictionary<ActivityState, string> ActivityLabels = new()
    {
        { ActivityState.Starting, "Starting" },
        { ActivityState.Idle, "Idle" },
        { ActivityState.Working, "Working" },
        { ActivityState.WaitingForInput, "Your Turn" },
        { ActivityState.WaitingForPerm, "Permission" },
        { ActivityState.Exited, "Exited" },
    };

    public Session Session { get; }

    public SessionViewModel(Session session)
    {
        Session = session;
        session.OnActivityStateChanged += OnActivityStateChanged;
    }

    public string DisplayName => Session.CustomName
        ?? Path.GetFileName(Session.RepoPath.TrimEnd('\\', '/'));

    public string? CustomColor
    {
        get => Session.CustomColor;
        set
        {
            Session.CustomColor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomColor));
            OnPropertyChanged(nameof(CustomColorBrush));
        }
    }

    public bool HasCustomColor => !string.IsNullOrWhiteSpace(CustomColor);

    public ISolidColorBrush CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CustomColor))
                return new SolidColorBrush(Colors.Transparent);
            try
            {
                var color = Color.Parse(CustomColor);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(Colors.Transparent);
            }
        }
    }

    public string ActivityLabel =>
        ActivityLabels.TryGetValue(Session.ActivityState, out var label) ? label : "Unknown";

    public ISolidColorBrush ActivityBrush =>
        ActivityBrushes.TryGetValue(Session.ActivityState, out var brush) ? brush : Brushes.Gray;

    public string RepoPath => Session.RepoPath;

    public void Rename(string? newName, string? color = null)
    {
        Session.CustomName = newName;
        if (color != null)
            Session.CustomColor = color;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CustomColor));
        OnPropertyChanged(nameof(HasCustomColor));
        OnPropertyChanged(nameof(CustomColorBrush));
    }

    private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ActivityLabel));
            OnPropertyChanged(nameof(ActivityBrush));
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
