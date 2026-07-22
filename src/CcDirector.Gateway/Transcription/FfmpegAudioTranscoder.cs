using System.Diagnostics;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Turns an arbitrary audio clip (WebM/Opus, MP4/AAC, MP3, ...) into a PCM WAV the
/// <see cref="BatchTranscriptionPipeline"/> can split into sub-4MB parts. The Gateway only knows how to
/// duration-split PCM WAV, so a long WebM/Opus recording over the provider's ~4MB request cap could not
/// be chunked at all before this (issue #1139: a 5m39s / 5.4MB WebM failed). Injected as an interface so
/// the pipeline can be unit-tested with a stub; the real one shells out to a bundled ffmpeg.
/// </summary>
public interface IAudioTranscoder
{
    /// <summary>
    /// Decode <paramref name="audio"/> to PCM WAV (16 kHz mono 16-bit). Throws
    /// <see cref="TranscriptionPermanentException"/> when the bytes cannot be decoded (unsupported or
    /// corrupt) - a permanent failure the caller must not retry.
    /// </summary>
    byte[] ToPcmWav(byte[] audio, string fileName, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IAudioTranscoder"/> backed by a bundled ffmpeg. ffmpeg ships beside the Gateway
/// executable (option (a), issue #1139) so transcode works on every machine without a system install.
/// Resolution order: the <c>CCDIRECTOR_FFMPEG</c> override (dev/tests), then <c>ffmpeg[.exe]</c> beside
/// the running assembly. A missing binary fails loud with instructions (no silent fallback to a
/// system ffmpeg, which would make behaviour machine-dependent).
/// </summary>
public sealed class FfmpegAudioTranscoder : IAudioTranscoder
{
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(3);
    private readonly Lazy<string> _ffmpegPath;

    /// <param name="ffmpegPath">Explicit ffmpeg path; resolves the bundled/override path lazily on first
    /// transcode when null. Resolution is deferred so merely CONSTRUCTING the transcoder (and therefore
    /// the pipeline) never fails when ffmpeg is absent and no clip actually needs transcoding.</param>
    public FfmpegAudioTranscoder(string? ffmpegPath = null)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath)
            ? new Lazy<string>(ResolveFfmpegPath)
            : new Lazy<string>(() => ffmpegPath);
    }

    /// <summary>The resolved ffmpeg path (bundled beside the Gateway or the CCDIRECTOR_FFMPEG override).
    /// Throws with instructions when neither exists - a missing binary is a build/deploy error.</summary>
    public static string ResolveFfmpegPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CCDIRECTOR_FFMPEG");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        // AppContext.BaseDirectory is the directory of the (single-file) Gateway exe - where the bundled
        // ffmpeg sits beside it. (Assembly.Location is empty under single-file publish, so it is not used.)
        var exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var beside = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(beside))
            return beside;

        throw new InvalidOperationException(
            $"ffmpeg was not found. The Gateway bundles it beside the executable as '{exeName}' "
            + "(issue #1139). Reinstall/redeploy the Gateway so ffmpeg is present, or set the "
            + "CCDIRECTOR_FFMPEG environment variable to an ffmpeg path.");
    }

    public byte[] ToPcmWav(byte[] audio, string fileName, CancellationToken ct = default)
    {
        if (audio is null || audio.Length == 0)
            throw new TranscriptionPermanentException(TranscriptionPermanentException.NonDecodable,
                "Cannot transcode empty audio.");

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
        var tmpIn = Path.Combine(Path.GetTempPath(), "cc-tx-" + Guid.NewGuid().ToString("N") + ext);
        var tmpOut = Path.Combine(Path.GetTempPath(), "cc-tx-" + Guid.NewGuid().ToString("N") + ".wav");

        try
        {
            File.WriteAllBytes(tmpIn, audio);
            RunFfmpeg(tmpIn, tmpOut, ct);

            if (!File.Exists(tmpOut) || new FileInfo(tmpOut).Length == 0)
                throw new TranscriptionPermanentException(TranscriptionPermanentException.NonDecodable,
                    "ffmpeg produced no audio - the clip could not be decoded.");

            var wav = File.ReadAllBytes(tmpOut);
            FileLog.Write($"[FfmpegAudioTranscoder] transcoded {audio.Length} bytes ({ext}) -> {wav.Length} bytes PCM WAV");
            return wav;
        }
        finally
        {
            TryDelete(tmpIn);
            TryDelete(tmpOut);
        }
    }

    private void RunFfmpeg(string input, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath.Value,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Decode anything ffmpeg understands to 16 kHz mono 16-bit PCM WAV - the transcription-friendly
        // form the splitter chunks. -nostdin so it never blocks waiting on input. `apad=pad_dur=0.6`
        // appends 0.6 s of trailing silence (the dictation end-word run-out, matching PcmWav.TrailingSilenceMs
        // and the browser wav.ts pad) so any compressed clip that reaches a transcode also gives the model
        // room to emit the last word.
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", input, "-ar", "16000", "-ac", "1", "-af", "apad=pad_dur=0.6", "-c:a", "pcm_s16le", "-f", "wav", output,
        })
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start ffmpeg at '{_ffmpegPath.Value}'.");

        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit((int)TranscodeTimeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException($"ffmpeg timed out after {TranscodeTimeout.TotalSeconds:0}s transcoding audio.");
        }

        if (proc.ExitCode != 0)
            throw new TranscriptionPermanentException(TranscriptionPermanentException.UnsupportedFormat,
                $"ffmpeg could not decode the audio (exit {proc.ExitCode}): {Truncate(stderr, 300)}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file cleanup is best effort */ }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
