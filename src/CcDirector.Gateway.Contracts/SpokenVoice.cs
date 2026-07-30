namespace CcDirector.Gateway.Speech;

/// <summary>
/// One voice the product can speak with (issue #1010) - an id the speech engine accepts, and the two
/// words a person needs to tell it apart from the others in the list.
///
/// A voice is the ONLY thing a language changes. That is the whole shape of this mission: the reverted
/// build made choosing a language switch the speech MODEL, and the multilingual engine it switched to
/// could not say the lengths this product writes (devthrottle_internal#547). So this type carries no
/// model, no engine, no endpoint and no rate - only which sound comes out of the one engine that already
/// serves English. There is nothing here for a future change to branch a model on.
/// </summary>
/// <param name="Id">The voice id the speech engine accepts verbatim, e.g. <c>ff_siwis</c>. Lower case,
///  and the only form stored or sent. An id the engine does not know returns 422 upstream, so the ids
///  here are the measured ones from the model registry and not a guess.</param>
/// <param name="Name">The voice's own name, capitalized for a settings screen: <c>Siwis</c>,
///  <c>Bella</c>, <c>George</c>. ASCII, because it is a user-interface label - see
///  <see cref="SpokenLanguage.NativeName"/> for why that is not in tension with the accents ruling.</param>
/// <param name="Description">How this voice differs from its neighbours in the same language, in the
///  fewest true words: <c>American female</c>, <c>British male</c>, <c>female</c>. Read by a person
///  choosing between twenty-eight English voices, so it says accent and gender and nothing else.</param>
public sealed record SpokenVoice(string Id, string Name, string Description);
