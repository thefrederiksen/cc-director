using System.IO;
using System.Text.Json;

namespace CcDirector.Wpf.Teams;

/// <summary>
/// Persists tunnel identity (TunnelId + ClusterId) to disk so the SDK
/// can reuse the same tunnel across application restarts.
/// </summary>
internal sealed class TunnelStateStore
{
    private readonly string _filePath;
    private readonly Action<string> _log;

    public TunnelStateStore(string filePath, Action<string> log)
    {
        _filePath = filePath;
        _log = log;
    }

    public record TunnelState(string TunnelId, string ClusterId);

    public TunnelState? Load()
    {
        _log($"[TunnelStateStore] Load: path={_filePath}");

        if (!File.Exists(_filePath))
        {
            _log("[TunnelStateStore] No state file found");
            return null;
        }

        var json = File.ReadAllText(_filePath);
        var state = JsonSerializer.Deserialize<TunnelState>(json);

        if (state is null || string.IsNullOrEmpty(state.TunnelId) || string.IsNullOrEmpty(state.ClusterId))
        {
            _log("[TunnelStateStore] State file is invalid, ignoring");
            return null;
        }

        _log($"[TunnelStateStore] Loaded: tunnelId={state.TunnelId}, clusterId={state.ClusterId}");
        return state;
    }

    public void Save(TunnelState state)
    {
        _log($"[TunnelStateStore] Save: tunnelId={state.TunnelId}, clusterId={state.ClusterId}");

        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);

        _log("[TunnelStateStore] State saved");
    }

    public void Delete()
    {
        _log("[TunnelStateStore] Delete");
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }
}
