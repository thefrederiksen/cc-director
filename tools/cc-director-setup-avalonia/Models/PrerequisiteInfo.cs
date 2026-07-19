using System.ComponentModel;

namespace CcDirectorSetup.Models;

public class PrerequisiteInfo : INotifyPropertyChanged
{
    private string _status = "Checking...";
    private string _version = "";
    private bool _isFound;

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsRequired { get; init; }

    /// <summary>
    /// True for a row that is checked, offered and explained, but never gates the wizard - the
    /// user is told on the Complete screen what skipping it costs. Distinct from merely
    /// "not required": Tailscale is optional (a deliberate choice, not a gap) and is NOT
    /// recommended, so it never appears in the Complete-screen capability notice.
    /// </summary>
    public bool IsRecommended { get; init; }

    /// <summary>
    /// The badge the user reads first: Required / Recommended / Optional. Bound by the row so the
    /// badge can never disagree with the classification (it used to say "Optional" for every
    /// non-required row, which made Claude Code indistinguishable from Tailscale).
    /// </summary>
    public string ImportanceLabel => IsRequired ? "Required" : (IsRecommended ? "Recommended" : "Optional");
    public required string InstallUrl { get; init; }

    /// <summary>Link to the CC Director install docs section for this prerequisite (setup help).</summary>
    public required string DocsUrl { get; init; }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(StatusColor)); }
    }

    public string Version
    {
        get => _version;
        set { _version = value; OnPropertyChanged(nameof(Version)); }
    }

    public bool IsFound
    {
        get => _isFound;
        set { _isFound = value; OnPropertyChanged(nameof(IsFound)); OnPropertyChanged(nameof(StatusColor)); }
    }

    public string StatusColor => IsFound ? "#22C55E" : (IsRequired ? "#CC4444" : "#888888");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
