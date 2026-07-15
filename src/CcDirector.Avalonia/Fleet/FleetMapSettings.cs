using CcDirector.ControlApi;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Fleet;

/// <summary>
/// Issue #1627: remembers whether this cc-director's fleet map is showing the whole fleet or only its own
/// sessions.
///
/// PER INSTALL, not per machine. The setting is keyed by the same exe-path slot
/// <see cref="DirectorIdStore"/> keys the Director's identity by, so cc-director.exe and cc-director1.exe
/// each remember their own answer - which is what "remember it for that cc-director" means on a machine
/// running several. It lives beside the id file, in the folder CcStorage owns; this class never re-derives
/// the storage root (see StorageRootGuardTests for why that rule exists).
///
/// The default is OFF - only this Director's sessions - and that is a decision, not an accident: those are
/// the only sessions a click here can actually open. A session on another Director has to go out to the
/// Cockpit, which is a different experience, so it is opt-in.
/// </summary>
public sealed class FleetMapSettings
{
    private readonly string _path;

    /// <param name="directory">
    /// Override the folder the setting file lives in. Tests pass an isolated temp directory so a real
    /// Director running on this machine is never read from (or written to). Production omits it.
    /// </param>
    /// <param name="slotKey">
    /// Override the slot key (normally this process's exe path). Tests pass a fixed value so the slot does
    /// not depend on the test host's exe path.
    /// </param>
    public FleetMapSettings(string? directory = null, string? slotKey = null)
    {
        var dir = directory ?? DirectorIdStore.DirectoryPath;
        var slot = DirectorIdStore.SlotFor(slotKey ?? DirectorIdStore.CurrentProcessSlotKey());
        _path = Path.Combine(dir, $"fleet-map-{slot}.txt");
    }

    /// <summary>The file this instance reads and writes. Exposed so a test can assert the slot keying.</summary>
    public string FilePath => _path;

    /// <summary>
    /// True when the map should show every Director's sessions. Absent file means the documented default
    /// (false) - a first run has no opinion yet, which is not an error.
    ///
    /// An unreadable or unrecognised file also yields the default, and says so in the log. That is a
    /// deliberate line: this is a view preference, and refusing to open the fleet map because a one-word
    /// file got corrupted would be a worse failure than opening it in the documented default state. The
    /// log line is what makes it diagnosable rather than silent.
    /// </summary>
    public bool LoadShowWholeFleet()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            var raw = File.ReadAllText(_path).Trim();
            if (string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(raw, "own", StringComparison.OrdinalIgnoreCase)) return false;
            FileLog.Write($"[FleetMapSettings] {_path} says \"{raw}\", which is neither \"all\" nor \"own\"; " +
                          "using the default (own sessions only).");
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMapSettings] could not read {_path}: {ex.Message}; using the default (own sessions only).");
            return false;
        }
    }

    /// <summary>Persist the choice. Failing to write is logged, not thrown: the map still works this run.</summary>
    public void SaveShowWholeFleet(bool showWholeFleet)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, showWholeFleet ? "all" : "own");
            FileLog.Write($"[FleetMapSettings] saved showWholeFleet={showWholeFleet} to {_path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMapSettings] could not write {_path}: {ex.Message}; the choice applies this run but will not be remembered.");
        }
    }
}
