using System.Text.Json;
using VoiceChat.Core.Logging;

namespace VoiceChat.Core.Stt;

/// <summary>
/// Stores custom words for STT correction. Provides case-correction post-processing
/// (Vosk outputs lowercase, this restores correct casing from dictionary entries).
/// Words are also passed to Vosk as phrase hints for improved recognition.
/// Thread-safe: dictionary can be updated from UI while pipeline is processing.
/// </summary>
public sealed class CustomDictionary
{
    private static readonly string DictionaryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "voice-chat",
        "custom-dictionary.json");

    private readonly object _lock = new();
    private string[] _words = [];

    /// <summary>
    /// Fired when words change so the pipeline can update Vosk phrase hints.
    /// </summary>
    public event Action? WordsChanged;

    public CustomDictionary()
    {
        VoiceLog.Write("[CustomDictionary] Creating. Path: " + DictionaryPath);
        try
        {
            Load();
        }
        catch (Exception ex)
        {
            VoiceLog.Write($"[CustomDictionary] Load FAILED: {ex.Message}");
            _words = [];
        }
    }

    public string[] GetWords()
    {
        lock (_lock)
        {
            return _words.ToArray();
        }
    }

    public void SetWords(string[] words)
    {
        VoiceLog.Write($"[CustomDictionary] SetWords: {words.Length} entries");
        lock (_lock)
        {
            _words = words
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .OrderByDescending(w => w.Split(' ').Length)
                .ToArray();
        }
        try
        {
            Save();
        }
        catch (Exception ex)
        {
            VoiceLog.Write($"[CustomDictionary] SetWords Save FAILED: {ex.Message}");
        }
        WordsChanged?.Invoke();
    }

    /// <summary>
    /// Corrects casing of transcription words that match dictionary entries.
    /// Vosk outputs lowercase; this restores correct casing (e.g. "soren" -> "Soren").
    /// Also strips [unk] tokens from Vosk grammar mode output.
    /// </summary>
    public string CorrectTranscription(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Strip [unk] tokens from Vosk grammar mode
        text = text.Replace("[unk]", " ");

        var inputWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (inputWords.Length == 0)
            return string.Empty;

        lock (_lock)
        {
            if (_words.Length == 0)
                return string.Join(" ", inputWords);
        }

        var result = new List<string>(inputWords.Length);
        var i = 0;

        lock (_lock)
        {
            while (i < inputWords.Length)
            {
                var matched = false;

                // Try multi-word entries first (longest match) -- _words is pre-sorted by SetWords/Load
                foreach (var entry in _words)
                {
                    var entryParts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (entryParts.Length <= 0 || i + entryParts.Length > inputWords.Length)
                        continue;

                    var allMatch = true;
                    for (var j = 0; j < entryParts.Length; j++)
                    {
                        if (!string.Equals(inputWords[i + j], entryParts[j], StringComparison.OrdinalIgnoreCase))
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch)
                    {
                        var original = string.Join(" ", inputWords.Skip(i).Take(entryParts.Length));
                        if (!string.Equals(original, entry, StringComparison.Ordinal))
                        {
                            VoiceLog.Write($"[CustomDictionary] Case corrected: \"{original}\" -> \"{entry}\"");
                        }
                        result.Add(entry);
                        i += entryParts.Length;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    result.Add(inputWords[i]);
                    i++;
                }
            }
        }

        return string.Join(" ", result);
    }

    private void Load()
    {
        if (!File.Exists(DictionaryPath))
        {
            VoiceLog.Write("[CustomDictionary] No dictionary file found, starting empty.");
            _words = [];
            return;
        }

        var json = File.ReadAllText(DictionaryPath);
        var data = JsonSerializer.Deserialize<DictionaryData>(json);
        if (data?.Words is null)
            throw new InvalidOperationException($"Dictionary file has no Words property: {DictionaryPath}");

        _words = data.Words
            .OrderByDescending(w => w.Split(' ').Length)
            .ToArray();
        VoiceLog.Write($"[CustomDictionary] Loaded {_words.Length} words from disk.");
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(DictionaryPath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {DictionaryPath}");
        Directory.CreateDirectory(dir);

        string[] snapshot;
        lock (_lock)
        {
            snapshot = _words.ToArray();
        }

        var json = JsonSerializer.Serialize(
            new DictionaryData { Words = snapshot },
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(DictionaryPath, json);
        VoiceLog.Write($"[CustomDictionary] Saved {snapshot.Length} words to disk.");
    }

    private sealed class DictionaryData
    {
        public string[] Words { get; set; } = [];
    }
}
