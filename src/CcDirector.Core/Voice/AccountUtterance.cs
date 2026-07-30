using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Speech;

namespace CcDirector.Core.Voice;

/// <summary>
/// HOW THE DESKTOP GETS AN UTTERANCE (issue #1031).
///
/// The desktop app speaks - the briefing and the wingman's answer - and it was the last place in the product
/// that decided for itself how. It resolved its voice from the process-global <c>config.json</c> value and took
/// no account at all, so an account set to French was read aloud by whatever voice the machine held: in
/// practice the English default. The words were right. The voice was wrong. Nothing errored.
///
/// It does not decide any more. It ASKS. The Gateway already owns this decision in one place - one resolver
/// call folds the account's language and its chosen voice into a single answer, and every Gateway speech path
/// reads it - so the desktop reads that same answer over
/// <c>GET /gateway/spoken-language</c> and packages it into the SAME <see cref="SpokenUtterance"/> type the
/// Gateway builds. One contract, one decider, two runtimes: the desktop is a sink like the others.
///
/// IT PRODUCES A VOICE AND NEVER A MODEL. The desktop's engine is resolved separately, exactly as it always
/// was, with no knowledge of any language. A language selects the voice inside the one engine that already
/// serves English; a language selecting an ENGINE is what got this feature reverted
/// (devthrottle_internal#547), which is why nothing on this path can carry one.
///
/// STANDALONE IS NOT A FAILURE AND NOT A FALLBACK. A Director with no Gateway configured has no account, so
/// there is no per-account language to have; <see cref="ForAsync"/> returns null and the caller speaks exactly
/// as it did before any of this existed, from the machine's own configuration. That is the complete and correct
/// answer for that topology, not a degraded one.
///
/// AN ATTACHED-BUT-UNREACHABLE GATEWAY CANNOT MAKE THE DESKTOP SPEAK IN THE WRONG VOICE, which is the case
/// that looks dangerous. Desktop speech also needs the account KEY, and that key comes from the same Gateway
/// through the same configuration (<see cref="HostedAiKeyResolver"/>). No Gateway means no key, which means no
/// audio: the two fail together. There is no state where this returns null quietly and a sentence is still
/// spoken.
/// </summary>
public class AccountUtterance
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly Func<GatewayConfig> _gatewayProvider;
    private readonly HttpClient _http;

    /// <param name="gatewayProvider">Supplies the current gateway configuration on demand. Read fresh on every
    ///  call rather than snapshotted, so a Director that booted standalone and later had a gateway address
    ///  written into its configuration starts speaking in the account's language without a restart - the same
    ///  live-read contract <see cref="HostedAiKeyResolver"/> follows, and for the same reason.</param>
    /// <param name="http">HTTP client for the fetch (tests inject a stub).</param>
    public AccountUtterance(Func<GatewayConfig>? gatewayProvider = null, HttpClient? http = null)
    {
        _gatewayProvider = gatewayProvider ?? GatewayConfig.Load;
        _http = http ?? SharedHttp;
    }

    /// <summary>
    /// The account's decision, packaged: <paramref name="text"/> with the language this account is spoken to in
    /// and the voice that speaks it. Null when there is no account to ask (see the standalone note above).
    ///
    /// ASKED EVERY TIME, AND DELIBERATELY NOT CACHED. The language and voice settings are defined to take
    /// effect on the next spoken output; a cache is precisely what would make the Language tab appear to do
    /// nothing for as long as it lived, and "the setting does nothing" was reported three times on the attempt
    /// that was reverted. One small request against a Gateway that is about to be asked to synthesize a whole
    /// sentence is not the cost worth optimizing here.
    /// </summary>
    public async Task<SpokenUtterance?> ForAsync(string text, CancellationToken ct = default)
    {
        var gateway = _gatewayProvider();
        if (!gateway.IsEnabled) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var url = gateway.Url.TrimEnd('/') + "/gateway/spoken-language";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(gateway.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[AccountUtterance] GET {url} -> {(int)resp.StatusCode}; speaking with the machine's own voice");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() : null;
            var voice = doc.RootElement.TryGetProperty("voice", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(voice))
            {
                FileLog.Write($"[AccountUtterance] GET {url} answered without a language or a voice; speaking with the machine's own voice");
                return null;
            }

            // Resolve, not parse-or-throw: a code from a NEWER Gateway that this build does not know reads as
            // English rather than taking the voice away entirely - the same direction SpokenLanguages.Resolve
            // documents for every read, and the reason a rollback stays safe.
            return SpokenUtterance.For(SpokenLanguages.Resolve(code), voice, text);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountUtterance] fetch failed ({url}): {ex.Message}");
            return null;
        }
    }
}
