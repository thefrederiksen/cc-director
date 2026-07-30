using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice;
using NAudio.Wave;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Speaks text aloud on the desktop. Generation reuses the shared in-process
/// <see cref="TtsService"/> (hosted /v1/audio/speech, returning mp3 bytes); this
/// class is only the playback half - it decodes the mp3 and plays it through
/// NAudio's default output device. One player serves the whole app: a new
/// <see cref="SpeakAsync"/> cuts off any speech still playing so replies never
/// pile up on top of each other.
///
/// Windows-only by construction (NAudio's Mp3FileReader uses the ACM mp3 codec
/// shipped with Windows), which is fine - the Director runs on Windows.
/// </summary>
public sealed class DesktopTtsPlayer : IDisposable
{
    private readonly AgentOptions _options;
    private readonly object _gate = new();
    private WaveOutEvent? _output;

    // Resolves the DevThrottle text-to-speech credential from the Gateway vault when attached, or the
    // local vault when standalone. Passed to TtsService so desktop speech honors the hosted voice setup.
    private readonly HostedAiKeyResolver _keyResolver = new();

    // Where an utterance comes from on the desktop (issue #1031): it ASKS the Gateway, which owns the one
    // language-and-voice decision, and packages the answer into the same type the Gateway's own sinks take.
    // This class decides nothing about how it sounds - it used to, from a machine-global file that no account
    // setting could reach, which is why a French account was read aloud in an English voice.
    private readonly AccountUtterance _utterances;

    public DesktopTtsPlayer(AgentOptions options, AccountUtterance? utterances = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _utterances = utterances ?? new AccountUtterance();
    }

    /// <summary>True when a text-to-speech credential is configured for the selected provider.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        new TtsService(_options, _keyResolver).IsAvailableAsync(ct);

    /// <summary>
    /// Generate audio for <paramref name="text"/> and play it to completion.
    /// Returns when playback finishes (or is cut off by a newer call / cancel).
    /// Returns <c>true</c> when audio was generated and handed to the output
    /// device, <c>false</c> when generation failed (no key, network, API error).
    /// A generation failure logs and returns quietly - a missing voice must never
    /// take down the caller's turn - but the bool lets a caller (e.g. the playback
    /// dialog) surface the failure instead of closing as if it had spoken.
    /// </summary>
    /// <param name="onUnavailable">Issue #940: invoked (with the shared state) when generation failed
    /// because hosted AI is out of credits / capped - so the caller can surface the ONE add-credits
    /// message instead of the failure being logged and silently swallowed. Not called for ordinary
    /// generation failures (no key, network), which stay quiet as before.</param>
    public async Task<bool> SpeakAsync(string text, CancellationToken ct = default, Action<HostedAiState>? onUnavailable = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var svc = new TtsService(_options, _keyResolver);
        // The account's decision, or null when this Director is standalone - no Gateway means no account, so
        // the machine's own configured voice is the only truth there and the legacy path is the right answer
        // rather than a degraded one.
        var lookup = await _utterances.ForAsync(text, ct);

        // AN ATTACHED DIRECTOR THAT CANNOT LEARN ITS ACCOUNT'S LANGUAGE REFUSES TO SPEAK (client audit,
        // finding 1). It does NOT fall back to the machine's voice, and the distinction is the whole fix: a
        // Director with no Gateway has no account and the machine voice is correct, but a Director that HAS an
        // account and could not ask does not know what language to speak - and guessing English is the bug this
        // mission exists to remove.
        //
        // It used to be one null covering both, and the reasoning that made it look safe - "speech needs the
        // account key from the same Gateway, so they fail together" - was wrong: the key is fetched by another
        // route and cached in memory. So the real sequence was a French account speaking once while healthy and
        // then having its next sentence read out in English because one lookup timed out. Silently. Returning
        // false here is not a swallowed failure: the caller shows it (the playback dialog surfaces it rather than
        // closing as if it had spoken) and the reason is in the log.
        if (lookup.HasAccount && lookup.Utterance is null)
        {
            FileLog.Write($"[DesktopTtsPlayer] NOT SPEAKING: this Director is attached to a Gateway but could not "
                          + $"resolve the account's spoken language - {lookup.Reason}. Speaking with the machine's "
                          + $"own voice could read another language's words aloud in the wrong one, so it does not "
                          + $"speak. chars={text.Length}");
            return false;
        }

        var result = lookup.Utterance is not null
            ? await svc.GenerateAsync(lookup.Utterance, ct)
            // No Gateway, so no account: the machine's own configured voice is the whole truth here.
            : await svc.GenerateAsync(text, voiceOverride: null, modelOverride: null, ct);
        if (!result.Success || result.AudioBytes is null)
        {
            FileLog.Write($"[DesktopTtsPlayer] TTS not played: status={result.Status} error={result.ErrorMessage}");
            // A 402 carries the machine code as its status (issue #940): map it to the shared state and
            // hand it to the caller so read-aloud out-of-credits is no longer a silent nothing.
            if (onUnavailable is not null && IsOutOfCreditsStatus(result.Status))
                onUnavailable(HostedAiErrorMapper.MapCode(result.Status));
            return false;
        }

        Stop(); // cut off anything still speaking before starting the new reply

        var audio = result.AudioBytes;
        await Task.Run(() =>
        {
            var ms = new MemoryStream(audio);
            var reader = new Mp3FileReader(ms);
            var output = new WaveOutEvent();
            lock (_gate) { _output = output; }
            try
            {
                output.Init(reader);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing)
                {
                    if (ct.IsCancellationRequested) break;
                    Thread.Sleep(80);
                }
            }
            finally
            {
                try { output.Stop(); } catch { /* already stopped/disposed */ }
                output.Dispose();
                reader.Dispose();
                ms.Dispose();
                lock (_gate) { if (ReferenceEquals(_output, output)) _output = null; }
            }
        }, ct);

        return true;
    }

    /// <summary>True when a failed generation's status is one of the out-of-credits / cap codes a 402
    /// carries (issue #940), so the shared add-credits state should be surfaced.</summary>
    private static bool IsOutOfCreditsStatus(string? status) =>
        status == HostedAiErrorMapper.InsufficientCreditsCode || status == HostedAiErrorMapper.MonthlyLimitReachedCode;

    /// <summary>Stop any in-progress playback immediately.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            try { _output?.Stop(); } catch { /* best effort */ }
        }
    }

    public void Dispose() => Stop();
}
