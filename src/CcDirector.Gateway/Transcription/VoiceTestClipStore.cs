using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Stores the clips produced by the Test microphone and Test transcription checks, per tenant, with a
/// metadata sidecar, so transcription quality can be studied after the fact.
///
/// WHY A NEW STORE RATHER THAN <see cref="TranscriptionAudioArchive"/>. That archive is a 24-hour
/// troubleshooting buffer for ORDINARY dictation, and it refuses to write at all on a hosted Gateway
/// because it has one process-wide directory with a global prune - which would mix every account's
/// speech at rest and let a busy tenant evict a quiet one's clips (MTR-10 Gap A). Both properties are
/// wrong for this feature: these clips exist to be compared over weeks, and they must work on hosted,
/// which is where the interesting variety of languages and headsets actually is. So this store carries
/// the tenant in its path from the very first write and prunes strictly WITHIN one tenant, which is
/// exactly the gap that disabled the other one.
///
/// WHY KEEPING THIS AUDIO IS DIFFERENT FROM KEEPING DICTATION. The repository's standing position is
/// that recorded speech is bounded everywhere ("there is no deployment where keeping it forever is
/// right"), and that is not weakened here. What changes is the CONTENT: a test clip is the user
/// deliberately reading a passage THIS PRODUCT PUT ON THEIR SCREEN. It contains no private message, no
/// customer name and no dictated work - only known words the user chose to record for the purpose of
/// being analysed. That is a materially different category from an intercepted utterance, and it is
/// why a longer window is defensible here and nowhere else. The window still exists.
///
/// RETENTION: 30 days and 200 clips per tenant, whichever bites first. The 30 days matches the window
/// <see cref="TranscriptStore"/> already applies to transcript text, so the product keeps one answer to
/// "how long do you hold on to my voice work" rather than two. The 200 is a per-tenant cap, so one
/// tenant running the check repeatedly can never prune another tenant's clips.
/// </summary>
public sealed class VoiceTestClipStore
{
    /// <summary>How long a stored clip is kept. Matches the transcript-text window deliberately.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Most clips kept for ONE tenant. Per tenant, never global - that distinction is the
    /// whole reason this store may run on hosted when the older archive may not.</summary>
    public const int MaxClipsPerTenant = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _directory;

    /// <summary>Root directory holding every tenant's partition.</summary>
    public static string DefaultDirectory() => CcStorage.VoiceTestClips();

    /// <summary>
    /// This tenant's partition. The tenant is in the PATH, not merely in a field, so there is no code
    /// path that can write a clip outside its own tenant's folder - a store instance simply cannot
    /// address another tenant's directory.
    /// </summary>
    public static string DirectoryFor(TenantId tenant)
    {
        var chars = tenant.Value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return Path.Combine(DefaultDirectory(), new string(chars));
    }

    /// <summary>A store bound to one tenant's partition.</summary>
    public static VoiceTestClipStore ForTenant(TenantId tenant) => new(DirectoryFor(tenant));

    public VoiceTestClipStore(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory();
    }

    /// <summary>The directory this instance writes to.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Save one clip and its sidecar. Returns the clip id, or null when nothing could be written -
    /// storing a diagnostic must never fail the check the user is running, so this reports rather than
    /// throws, and the caller still returns the transcript it already has.
    /// </summary>
    public string? TrySave(VoiceTestClip clip, byte[] audio, string contentType)
    {
        if (clip is null) throw new ArgumentNullException(nameof(clip));
        if (audio is null || audio.Length == 0) return null;

        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_directory);
                var extension = GatewayTranscriptionService.ExtensionFor(contentType);
                var audioPath = Path.Combine(_directory, $"clip-{SafeName(clip.ClipId)}.{extension}");
                File.WriteAllBytes(audioPath, audio);

                // The sidecar carries everything needed to interpret the clip later: which check it
                // came from, which language, the passage the user was asked to read, and what came
                // back. Stored beside the audio rather than in a table so an analyst can copy one
                // folder and have the complete picture, audio included.
                var sidecarPath = Path.Combine(_directory, $"clip-{SafeName(clip.ClipId)}.json");
                File.WriteAllText(sidecarPath, JsonSerializer.Serialize(clip, JsonOptions));

                Prune();
                FileLog.Write(
                    $"[VoiceTestClipStore] saved clip={clip.ClipId} kind={clip.Kind} language={clip.Language} " +
                    $"bytes={audio.Length} dir={_directory}");
                return clip.ClipId;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTestClipStore] TrySave FAILED (swallowed): {ex.Message}");
            return null;
        }
    }

    /// <summary>Every stored clip's metadata for this tenant, newest first. The analysis read.</summary>
    public IReadOnlyList<VoiceTestClip> List()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(_directory)) return Array.Empty<VoiceTestClip>();
            var clips = new List<VoiceTestClip>();
            foreach (var file in new DirectoryInfo(_directory).GetFiles("clip-*.json"))
            {
                try
                {
                    var clip = JsonSerializer.Deserialize<VoiceTestClip>(File.ReadAllText(file.FullName), JsonOptions);
                    if (clip is not null) clips.Add(clip);
                }
                catch (Exception ex)
                {
                    // One unreadable sidecar must not hide every other clip.
                    FileLog.Write($"[VoiceTestClipStore] skipping unreadable sidecar {file.Name}: {ex.Message}");
                }
            }
            return clips.OrderByDescending(c => c.RecordedAtUtc).ToList();
        }
    }

    /// <summary>Delete every clip in this tenant's partition. Returns how many clips were removed.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(_directory)) return 0;
            var removed = 0;
            foreach (var file in new DirectoryInfo(_directory).GetFiles("clip-*"))
            {
                try
                {
                    file.Delete();
                    if (file.Extension == ".json") removed++;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[VoiceTestClipStore] could not delete {file.Name}: {ex.Message}");
                }
            }
            return removed;
        }
    }

    /// <summary>
    /// Enforce both bounds inside THIS tenant's directory. Called under the lock on every save. A clip
    /// and its sidecar are deleted together, so a listing can never show metadata whose audio is gone.
    /// </summary>
    private void Prune()
    {
        if (!System.IO.Directory.Exists(_directory)) return;

        var sidecars = new DirectoryInfo(_directory)
            .GetFiles("clip-*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        var cutoff = DateTime.UtcNow - MaxAge;
        var doomed = sidecars.Where((f, index) => index >= MaxClipsPerTenant || f.LastWriteTimeUtc < cutoff).ToList();

        foreach (var sidecar in doomed)
        {
            var stem = Path.GetFileNameWithoutExtension(sidecar.Name);
            foreach (var companion in new DirectoryInfo(_directory).GetFiles(stem + ".*"))
            {
                try
                {
                    companion.Delete();
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[VoiceTestClipStore] prune could not delete {companion.Name}: {ex.Message}");
                }
            }
        }
    }

    private static string SafeName(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }
}

/// <summary>Which check produced a stored clip.</summary>
public static class VoiceTestKind
{
    /// <summary>The microphone check: no transcription, only the recorded audio and its measurements.</summary>
    public const string Microphone = "microphone";

    /// <summary>The transcription check: a known passage read aloud, transcribed and compared.</summary>
    public const string Transcription = "transcription";

    public static bool IsValid(string? kind) => kind is Microphone or Transcription;
}

/// <summary>
/// The metadata sidecar stored beside a test clip.
///
/// It deliberately holds the EXPECTED passage and the RAW transcript rather than a score. A score is
/// one interpretation, computed by one version of one scorer; the passage and the transcript are the
/// evidence, and any future question - a different scoring rule, a per-word breakdown, a comparison
/// across releases - can still be answered from them. Storing only a number would foreclose all of it.
/// </summary>
public sealed record VoiceTestClip
{
    /// <summary>Identifier shared by the audio file and this sidecar.</summary>
    public required string ClipId { get; init; }

    /// <summary>"microphone" or "transcription" (see <see cref="VoiceTestKind"/>).</summary>
    public required string Kind { get; init; }

    /// <summary>When the clip was received.</summary>
    public required DateTime RecordedAtUtc { get; init; }

    /// <summary>BCP 47 primary subtag the user selected, and the hint sent to the transcriber.</summary>
    public string? Language { get; init; }

    /// <summary>The passage the user was asked to read. Null for a microphone check.</summary>
    public string? ExpectedText { get; init; }

    /// <summary>What the transcriber returned. Null for a microphone check, or when it failed.</summary>
    public string? Transcript { get; init; }

    /// <summary>Why transcription did not produce text, when it did not.</summary>
    public string? Outcome { get; init; }

    /// <summary>The client's own audio measurements, verbatim, so a clip can be studied without
    /// re-deriving them. Free-form on purpose: the microphone check owns their shape, and this store
    /// must not become a second place that has to be edited when a measurement is added.</summary>
    public JsonElement? Quality { get; init; }

    /// <summary>Size of the stored audio in bytes.</summary>
    public long AudioBytes { get; init; }

    /// <summary>The clip's MIME type.</summary>
    public string? ContentType { get; init; }
}
