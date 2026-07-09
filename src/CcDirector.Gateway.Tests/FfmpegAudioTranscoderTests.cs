using System.Diagnostics;
using CcDirector.Core.Audio;
using CcDirector.Core.Transcription;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Integration proof for the #1139 transcode: a real WebM/Opus clip (the incident's format) is decoded
/// to a PCM WAV the splitter can chunk. Runs a real ffmpeg, so it is gated on ffmpeg being resolvable
/// (the bundled binary in production, or CCDIRECTOR_FFMPEG / PATH here) and SKIPS cleanly otherwise, so
/// CI without ffmpeg stays green. On this dev machine (ffmpeg present) it actually exercises the path.
/// </summary>
public sealed class FfmpegAudioTranscoderTests
{
    [Fact]
    public void RealFfmpeg_TranscodesWebmOpus_ToSplittablePcmWav()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return; // ffmpeg unavailable - skip (no assertion), never fail CI

        var webmPath = Path.Combine(Path.GetTempPath(), "cc-ffmpeg-test-" + Guid.NewGuid().ToString("N") + ".webm");
        try
        {
            // Synthesize ~3 seconds of Opus-in-WebM, exactly what a browser MediaRecorder produces.
            RunOrThrow(ffmpeg, $"-hide_banner -loglevel error -y -f lavfi -i sine=frequency=440:duration=3 -c:a libopus \"{webmPath}\"");
            var webm = File.ReadAllBytes(webmPath);
            Assert.True(webm.Length > 0, "ffmpeg did not produce a WebM clip");

            var wav = new FfmpegAudioTranscoder(ffmpeg).ToPcmWav(webm, "clip.webm");

            // The output is a real PCM WAV the existing splitter can parse and chunk.
            Assert.True(wav.Length > 44, "transcoded WAV is only a header");
            var ok = WavSplitter.TrySplitByDuration(
                wav, BatchTranscriptionPipeline.ChunkTargetSeconds, BatchTranscriptionPipeline.ChunkMaxSeconds,
                BatchTranscriptionPipeline.ChunkSilenceWindowSeconds, BatchTranscriptionPipeline.MaxTranscriptionUploadBytes,
                out var parts);
            Assert.True(ok && parts is { Count: > 0 }, "transcoded WAV was not splittable");
        }
        finally
        {
            try { if (File.Exists(webmPath)) File.Delete(webmPath); } catch { }
        }
    }

    [Fact]
    public void RealFfmpeg_UndecodableBytes_ThrowsPermanent()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var junk = new byte[2048];
        for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i % 251); // not any real audio container

        var ex = Assert.Throws<TranscriptionPermanentException>(
            () => new FfmpegAudioTranscoder(ffmpeg).ToPcmWav(junk, "clip.webm"));
        Assert.False(ex.IsTransient);
    }

    private static string? FindFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("CCDIRECTOR_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        try
        {
            var which = OperatingSystem.IsWindows() ? "where" : "which";
            var psi = new ProcessStartInfo(which, "ffmpeg") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var first = p.StandardOutput.ReadLine();
            p.WaitForExit(5000);
            return !string.IsNullOrWhiteSpace(first) && File.Exists(first.Trim()) ? first.Trim() : null;
        }
        catch { return null; }
    }

    private static void RunOrThrow(string ffmpeg, string args)
    {
        var psi = new ProcessStartInfo(ffmpeg, args) { RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg setup failed: {err}");
    }
}
