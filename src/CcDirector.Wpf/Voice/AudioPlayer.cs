using System.IO;
using NAudio.Wave;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Voice;

/// <summary>
/// Audio player using NAudio WaveOutEvent.
/// Plays WAV files for TTS output.
/// </summary>
public class AudioPlayer : IDisposable
{
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _audioReader;
    private bool _disposed;

    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    /// <summary>
    /// Fires when playback completes.
    /// </summary>
    public event Action? OnPlaybackComplete;

    /// <summary>
    /// Play a WAV file.
    /// </summary>
    /// <param name="filePath">Path to the WAV file.</param>
    public void Play(string filePath)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        FileLog.Write($"[AudioPlayer] Play: {filePath}");

        // Stop any current playback
        Stop();

        if (!File.Exists(filePath))
        {
            FileLog.Write($"[AudioPlayer] File not found: {filePath}");
            throw new FileNotFoundException("Audio file not found", filePath);
        }

        try
        {
            _audioReader = new AudioFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Init(_audioReader);
            _waveOut.Play();

            FileLog.Write($"[AudioPlayer] Playing: duration={_audioReader.TotalTime}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AudioPlayer] Play FAILED: {ex.Message}");
            CleanupPlayback();
            throw;
        }
    }

    /// <summary>
    /// Play a WAV file asynchronously.
    /// Returns when playback completes.
    /// </summary>
    public Task PlayAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource();

        void OnComplete()
        {
            OnPlaybackComplete -= OnComplete;
            tcs.TrySetResult();
        }

        using var registration = cancellationToken.Register(() =>
        {
            OnPlaybackComplete -= OnComplete;
            Stop();
            tcs.TrySetCanceled();
        });

        OnPlaybackComplete += OnComplete;
        Play(filePath);

        return tcs.Task;
    }

    /// <summary>
    /// Stop current playback.
    /// </summary>
    public void Stop()
    {
        FileLog.Write("[AudioPlayer] Stop");
        _waveOut?.Stop();
        CleanupPlayback();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        FileLog.Write("[AudioPlayer] PlaybackStopped");

        if (e.Exception != null)
        {
            FileLog.Write($"[AudioPlayer] Playback error: {e.Exception.Message}");
        }

        CleanupPlayback();
        OnPlaybackComplete?.Invoke();
    }

    private void CleanupPlayback()
    {
        if (_waveOut != null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Dispose();
            _waveOut = null;
        }

        _audioReader?.Dispose();
        _audioReader = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }
}
