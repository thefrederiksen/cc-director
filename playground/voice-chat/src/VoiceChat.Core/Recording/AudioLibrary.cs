using VoiceChat.Core.Logging;

namespace VoiceChat.Core.Recording;

/// <summary>
/// Saves recorded audio clips to disk as WAV files for offline testing.
/// Each recording is saved with a timestamp and the transcription result.
/// Location: %LOCALAPPDATA%\voice-chat\recordings\
/// </summary>
public static class AudioLibrary
{
    private static readonly string RecordingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "voice-chat", "recordings");

    public static string RecordingsPath => RecordingsDir;

    /// <summary>
    /// Saves a 16-bit PCM audio buffer as a WAV file and an accompanying .txt with the transcription.
    /// Returns the saved WAV file path.
    /// </summary>
    public static string Save(byte[] pcmAudio, int sampleRate, int bitsPerSample, int channels, string? transcription = null)
    {
        Directory.CreateDirectory(RecordingsDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var wavPath = Path.Combine(RecordingsDir, $"{timestamp}.wav");

        WriteWav(wavPath, pcmAudio, sampleRate, bitsPerSample, channels);
        VoiceLog.Write($"[AudioLibrary] Saved recording: {wavPath} ({pcmAudio.Length} bytes)");

        if (!string.IsNullOrWhiteSpace(transcription))
        {
            var txtPath = Path.ChangeExtension(wavPath, ".txt");
            File.WriteAllText(txtPath, transcription);
            VoiceLog.Write($"[AudioLibrary] Saved transcription: {txtPath}");
        }

        return wavPath;
    }

    /// <summary>
    /// Lists all WAV files in the recordings directory.
    /// </summary>
    public static string[] ListRecordings()
    {
        if (!Directory.Exists(RecordingsDir)) return [];
        return Directory.GetFiles(RecordingsDir, "*.wav", SearchOption.TopDirectoryOnly);
    }

    private static void WriteWav(string path, byte[] pcmData, int sampleRate, int bitsPerSample, int channels)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write(36 + pcmData.Length);
        writer.Write("WAVE"u8);

        // fmt chunk
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);              // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write("data"u8);
        writer.Write(pcmData.Length);
        writer.Write(pcmData);
    }
}
