using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The single source of truth for WHERE a tenant's dictation glossary lives and how it is loaded and
/// additively edited on the Gateway. The dictionary editor routes (<c>GET/PUT/POST /ingest/dictionary</c>),
/// the recording transcriber, and the suggestion "apply" path all resolve the same per-tenant
/// <c>dictionary.yaml</c> through here, so a term added on one path biases every other path for that tenant -
/// and one tenant's glossary is physically partitioned from another's.
///
/// PER-TENANT PATH: the single Local tenant keeps the existing shared flat file (so a self-host install's
/// glossary does not move); every other tenant gets its own <c>&lt;tenantFolder&gt;/dictionary.yaml</c> under the
/// dictation directory. Identical to the path <see cref="Api.RecordingEndpoints"/> has always used - that
/// class now delegates here so the two can never drift.
/// </summary>
public static class TenantGlossary
{
    /// <summary>This tenant's dictation glossary file. Local -> the shared flat file; every other tenant -> its
    /// own per-tenant file. The exact path the dictionary editor routes and the transcriber use.</summary>
    public static string PathFor(TenantId tenant)
        => tenant == TenantId.Local
            ? GatewayTranscriptionService.DictionaryPath()
            : Path.Combine(
                Path.GetDirectoryName(GatewayTranscriptionService.DictionaryPath())!,
                TenantFolder(tenant), "dictionary.yaml");

    /// <summary>Load this tenant's glossary, or <see cref="DictationDictionary.Empty"/> when it has none.</summary>
    public static DictationDictionary Load(TenantId tenant)
        => DictionaryLoader.LoadFromDisk(PathFor(tenant));

    /// <summary>
    /// Additively add vocabulary terms and their observed wrong spellings to this tenant's glossary - the
    /// write the suggestion "apply" performs. Existing entries are preserved and duplicates ignored (case-
    /// insensitive within a term's variants), so applying is idempotent. Each applied term is written into
    /// BOTH Vocabulary (the term itself) AND Common mistranscriptions (its wrong spellings, so the cleanup
    /// pass corrects them), which is the whole payoff: one press delivers the full fix. Returns the re-read
    /// glossary.
    /// </summary>
    /// <param name="tenant">The tenant the endpoint resolved. Required and valid.</param>
    /// <param name="vocabulary">Canonical terms to add to Vocabulary. May be empty.</param>
    /// <param name="mistranscriptions">Canonical term -> its wrong spellings, added to Common
    /// mistranscriptions. May be empty.</param>
    public static DictationDictionary AddTerms(
        TenantId tenant,
        IReadOnlyList<string> vocabulary,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mistranscriptions)
    {
        // Through TenantGlossaryWriter, so the read and the write are ONE step no other writer can
        // interleave with. Unguarded, this could load the document, be overtaken by the person's Cockpit
        // save, and write its now-stale copy back over it - erasing a wrong-spellings list they had just
        // edited (found reviewing #2484).
        return TenantGlossaryWriter.Mutate(tenant, current => Merge(current, vocabulary, mistranscriptions));
    }

    /// <summary>
    /// The pure merge - existing document in, updated document out. Split from the write so the additive
    /// rule can be read and tested without a file system, and so the ONLY thing left around the file access
    /// is the lock. It must derive everything from <paramref name="current"/>: it runs inside the lock and
    /// possibly after a wait, so a value captured before the wait is exactly the stale copy that caused the
    /// defect.
    /// </summary>
    internal static DictationDictionary Merge(
        DictationDictionary current,
        IReadOnlyList<string> vocabulary,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mistranscriptions)
    {
        var vocab = current.Vocabulary.ToList();
        foreach (var term in vocabulary)
        {
            var trimmed = term?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) &&
                !vocab.Any(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase)))
                vocab.Add(trimmed);
        }

        var patterns = current.CommonMistranscriptions
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.Ordinal);
        foreach (var kv in mistranscriptions)
        {
            var term = kv.Key?.Trim();
            if (string.IsNullOrWhiteSpace(term) || kv.Value is null)
                continue;
            var variants = patterns.TryGetValue(term, out var existing) ? existing.ToList() : new List<string>();
            foreach (var v in kv.Value)
            {
                var vv = v?.Trim();
                if (!string.IsNullOrWhiteSpace(vv) &&
                    !variants.Any(x => string.Equals(x, vv, StringComparison.OrdinalIgnoreCase)))
                    variants.Add(vv);
            }
            if (variants.Count > 0)
                patterns[term] = variants;
        }

        return new DictationDictionary(vocab, patterns, current.Profiles);
    }

    /// <summary>A filesystem-safe folder name for a tenant partition - identical to the sanitiser
    /// <see cref="Api.RecordingEndpoints"/> uses for a tenant's recordings, so the two agree on the folder.</summary>
    internal static string TenantFolder(TenantId tenant)
    {
        var chars = tenant.Value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }
}
