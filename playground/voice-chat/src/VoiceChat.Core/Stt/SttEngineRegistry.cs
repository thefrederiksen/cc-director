namespace VoiceChat.Core.Stt;

/// <summary>
/// Registry of available STT engines. Holds all registered engines
/// and provides lookup by display name.
/// </summary>
public sealed class SttEngineRegistry
{
    private readonly List<ISttEngine> _engines = [];

    public void RegisterEngine(ISttEngine engine)
    {
        _engines.Add(engine);
    }

    public IReadOnlyList<ISttEngine> GetEngines() => _engines;

    public ISttEngine? GetEngine(string displayName)
    {
        return _engines.Find(e => e.DisplayName == displayName);
    }
}
