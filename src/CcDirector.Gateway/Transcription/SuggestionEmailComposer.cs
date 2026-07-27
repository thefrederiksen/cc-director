using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Decides whether this tenant's daily report should carry a dictionary-suggestions block right now, and
/// renders it when the answer is yes (issue #2074, mockup screen 5).
///
/// THE GATEWAY OWNS THE VERDICT (critical rule 7). Whatever composes the daily report asks this one question
/// and renders the answer verbatim; it never re-derives "are there suggestions", "is the setting on", or "have
/// we already said this". Every reason to stay quiet is folded here and named in
/// <see cref="EmailBlockDecision.Reason"/>, so a report that omits the block can always say why - which is what
/// makes a missing block debuggable instead of a mystery.
///
/// THE FOUR REASONS TO STAY QUIET, in the order they are checked:
///   1. The tenant turned "Suggestions in my daily email" off. Their choice, checked first so nothing else runs.
///   2. There are no pending suggestions. Nothing to say; the block simply is not there (screen 5, note 1).
///   3. This exact batch has already been mentioned <see cref="DictationEmailCadenceState.MaxMentionsPerBatch"/>
///      times. The badge on the Dictionary page is the durable signal; the email is a doorbell and does not
///      keep ringing.
///   4. Nothing - the block is included.
///
/// MENTIONING IS AN EXPLICIT COMMIT, not a side effect of asking. <see cref="Compose"/> answers without writing
/// anything unless <c>markMentioned</c> is set, so a caller can preview the block (a settings page, a dry run,
/// a test) without spending one of the batch's two mentions. Only the caller that is actually about to SEND
/// commits the count.
/// </summary>
public sealed class SuggestionEmailComposer
{
    private readonly Func<TenantId, IReadOnlyList<MistranscriptionSuggestion>> _pendingFor;
    private readonly TenantSettingsResolver _settings;
    private readonly Func<string?> _baseUrl;
    private readonly Func<DateTime> _now;

    /// <param name="pendingFor">The tenant's pending suggestions - production passes
    /// <see cref="DictionarySuggestionService.GetSuggestions"/>. Deliberately the LIST and not the whole
    /// service: this class reads what is already stored and never triggers a scan, and a narrow dependency is
    /// what makes that impossible to get wrong later. Required.</param>
    /// <param name="settings">The per-tenant settings the toggle and cadence state live in. Required.</param>
    /// <param name="baseUrl">Resolves this Gateway's public BASE address, or null when it has no remotely
    /// reachable one; production passes <see cref="GatewayPublicUrl.ResolveBase()"/>. The base, not the
    /// <c>{base}/cockpit</c> surface URL: the Cockpit's router is mounted at the root, so its Dictionary page
    /// is <c>{base}/dictionary</c> - a <c>{base}/cockpit/dictionary</c> link would be swallowed by the
    /// <c>/cockpit/{sessionId}</c> route and land the reader nowhere. A null means the block renders with no
    /// link rather than with a dead localhost one.</param>
    /// <param name="now">Clock for the cadence timestamp; <see cref="DateTime.UtcNow"/> when null.</param>
    public SuggestionEmailComposer(
        Func<TenantId, IReadOnlyList<MistranscriptionSuggestion>> pendingFor,
        TenantSettingsResolver settings,
        Func<string?> baseUrl,
        Func<DateTime>? now = null)
    {
        _pendingFor = pendingFor ?? throw new ArgumentNullException(nameof(pendingFor));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Why the block is or is not in this report. A closed set, so the caller renders a verdict rather
    /// than interpreting one.</summary>
    public enum BlockReason
    {
        /// <summary>The block is included.</summary>
        Included,

        /// <summary>The tenant turned "Suggestions in my daily email" off.</summary>
        SettingOff,

        /// <summary>There are no pending suggestions to mention.</summary>
        NoSuggestions,

        /// <summary>This batch has already had its mentions; the badge carries it from here.</summary>
        AlreadyMentioned,
    }

    /// <summary>
    /// The composed answer. <see cref="Block"/> is non-null exactly when <see cref="Include"/> is true, so a
    /// caller cannot render a block the decision said to withhold.
    /// </summary>
    /// <param name="Include">Whether the daily report should carry the block.</param>
    /// <param name="Reason">Why - including why not.</param>
    /// <param name="Block">The rendered block when included; null otherwise.</param>
    /// <param name="TermCount">How many terms are pending, whether or not the block is included.</param>
    /// <param name="Batch">The batch fingerprint; empty when there are no suggestions.</param>
    /// <param name="Mentions">How many times this batch has now been mentioned (after any commit).</param>
    public sealed record EmailBlockDecision(
        bool Include,
        BlockReason Reason,
        SuggestionEmailBlock.Rendered? Block,
        int TermCount,
        string Batch,
        int Mentions);

    /// <summary>
    /// Decide and, when included and <paramref name="markMentioned"/> is set, record the mention.
    /// </summary>
    /// <param name="tenant">The tenant whose report is being composed. Required and valid.</param>
    /// <param name="markMentioned">True only from the caller that is actually sending, so a preview never
    /// spends one of the batch's mentions.</param>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public EmailBlockDecision Compose(TenantId tenant, bool markMentioned)
    {
        if (!_settings.SuggestionsInDailyEmail(tenant))
            return new EmailBlockDecision(false, BlockReason.SettingOff, null, 0, "", 0);

        var suggestions = _pendingFor(tenant);
        if (suggestions.Count == 0)
            return new EmailBlockDecision(false, BlockReason.NoSuggestions, null, 0, "", 0);

        var batch = SuggestionEmailBlock.Fingerprint(suggestions);
        var cadence = _settings.DictationEmailCadence(tenant);
        if (!cadence.MayMention(batch))
        {
            FileLog.Write($"[SuggestionEmailComposer] quiet tenant={tenant.ToLogString()} batch={batch} " +
                          $"mentions={cadence.Mentions} - already at the cap");
            return new EmailBlockDecision(false, BlockReason.AlreadyMentioned, null, suggestions.Count, batch, cadence.Mentions);
        }

        var url = BuildDictionaryUrl();
        var rendered = SuggestionEmailBlock.Render(suggestions, url);

        var mentions = cadence.Mentions;
        if (markMentioned)
        {
            var nowUtc = _now().ToUniversalTime();
            var next = cadence.Mentioned(batch, nowUtc);
            _settings.SetDictationEmailCadence(tenant, next, nowUtc);
            mentions = next.Mentions;
            FileLog.Write($"[SuggestionEmailComposer] mentioned tenant={tenant.ToLogString()} batch={batch} " +
                          $"terms={suggestions.Count} mention={mentions} of {DictationEmailCadenceState.MaxMentionsPerBatch}");
        }

        return new EmailBlockDecision(true, BlockReason.Included, rendered, suggestions.Count, batch, mentions);
    }

    /// <summary>
    /// The absolute link to the Dictionary page, or null when this Gateway has no publicly reachable address.
    /// Null is a truthful answer, not a failure: a link to 127.0.0.1 in a message read on a phone is a dead
    /// link, and the block is written to name the page instead when there is nothing real to point at.
    /// </summary>
    private string? BuildDictionaryUrl()
    {
        var root = _baseUrl();
        if (string.IsNullOrWhiteSpace(root)) return null;
        return root.TrimEnd('/') + SuggestionEmailBlock.DictionaryPath;
    }
}
