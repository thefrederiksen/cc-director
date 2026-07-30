namespace CcDirector.Gateway.Speech;

/// <summary>
/// One language the product SPEAKS. Not a locale, not a translation of the user interface - the
/// language every spoken path talks in for one account (issue #1008).
///
/// A language is a VOICE, never an engine. The previous attempt at this (reverted by product pull
/// request 2181, post-mortem devthrottle_internal#547) made choosing a language switch the speech
/// MODEL to a multilingual engine, and that engine could not say the lengths this product writes:
/// French returned silence at 155 characters, Spanish blew a sixty-second deadline at 208, and the
/// wingman is tuned to write about 500. Nothing in this type, or anywhere downstream of it, selects
/// a speech model. French and Spanish are voices inside the same engine that already serves English,
/// measured on 2026-07-29 at 1.31 s and 1.24 s for a ~500-character narration against English's
/// 1.29 s. If a future change starts branching on <see cref="Code"/> to pick a model, that is the
/// reverted failure returning.
/// </summary>
/// <param name="Code">The short language code stored per account and carried on the wire:
///  <c>en</c>, <c>fr</c>, <c>es</c>. Lower case, and the only form persisted.</param>
/// <param name="EnglishName">The language's name in English - the word the SPOKEN OUTPUT CONTRACT
///  puts in front of the model ("SPEAK ENTIRELY IN FRENCH").</param>
/// <param name="NativeName">The language's name in its own language, for the settings screen. Held
///  to plain ASCII deliberately: it is a label in a user interface and a log line, not spoken
///  content, and the repository's output rule is ASCII everywhere. Spoken CONTENT is a different
///  thing and carries its own accents.</param>
public sealed record SpokenLanguage(string Code, string EnglishName, string NativeName);
