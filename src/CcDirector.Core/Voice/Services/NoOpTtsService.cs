using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// No-op text-to-speech service for when TTS is not available.
/// Does nothing but marks itself as available so the voice flow continues.
/// </summary>
public class NoOpTtsService : ITextToSpeech
{
    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public Task SynthesizeAsync(string text, string outputPath, CancellationToken cancellationToken = default)
    {
        // Do nothing - TTS is not available
        return Task.CompletedTask;
    }
}
