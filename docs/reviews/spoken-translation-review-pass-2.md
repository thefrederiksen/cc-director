<!-- The second-model translation review required by issue #1009. The owner's decision was
     explicit: one model translates, a second separately-prompted model reviews, and there is NO
     native human reviewer, because nobody in-house reads French or Spanish. That is an ACCEPTED
     RISK, recorded knowingly - if French or Spanish quality is ever reported as poor, this is the
     first place to look.

     The reviewer was given the English source and the proposed translations only. It was not told
     what the code does, what the intent was, or that the translations were ours. Pass 1 found 33
     problems including a register break in the French help script and a Spanish accent error that
     would have made the speech engine mispronounce a word; pass 2 ran over the corrected set and
     judged both languages shippable with one accuracy defect left, which was then fixed. -->
<!-- HOW ACCENTS ARE WRITTEN IN THIS FILE. Every accented letter is written as its base letter followed
     by the name of the mark in square brackets: e[acute] is e-acute, a[grave] is a-grave, n[tilde] is
     n-tilde, c[cedilla] is c-cedilla, and [inverted-question-mark] is the Spanish opening question mark.
     It reads worse than the real characters and it is deliberate.

     Documentation is a named ASCII-only channel (docs/MISSION-multilingual-RULINGS.md, Ruling 1): the
     accent exemption covers spoken CONTENT - the strings themselves, what is sent to the speech engine,
     and the test fixtures asserting on them - and nothing else. The inspection of Phases 1 and 2 found
     this file breaking that boundary on 123 lines.

     The alternative was to strip the accents, and that would have destroyed the record: this is a
     review OF accents, among other things, and "the accent on this word was wrong" is unreadable if the
     accents are gone. The gloss keeps every original character recoverable exactly, which a strip does
     not. -->

# Translation review - spoken strings (French, Spanish)

Reviewed: all 12 entries in review-packet-final.md, both languages (24 strings).
Judged as speech: accuracy, naturalness heard aloud, accents and spelling, agreement,
register consistency, punctuation that reads awkwardly.

Total problems found: 14 (6 French, 7 Spanish, 1 cross-cutting).

---

## What is already right (so the findings are read in proportion)

- Register is clean and consistent. French is "vous" in every entry (votre, vous, -ez
  imperatives, "Veuillez"); Spanish is "tu" in every entry (tu gestor, puedes, Abre,
  tendras -> "tendras" written correctly as "tendra[acute]s", por ti, tu ma[acute]quina, Di). No leakage
  of "usted" or "tutoiement" anywhere.
- Every Spanish imperative with an attached pronoun carries its accent, and all of them are
  correct: "Inte[acute]ntalo" (in-te[acute]n-ta-lo, esdru[acute]jula), "pi[acute]deme" (pi[acute]-de-me), "apla[acute]zala"
  (a-pla[acute]-za-la), "aprue[acute]bala" (accent on the strong vowel of the diphthong), "elimi[acute]nala"
  (e-li-mi[acute]-na-la, sobresdru[acute]jula), "dile" (llana, correctly unaccented), "elige"
  (correctly unaccented). Nothing here will be mispronounced by the phonemizer.
- Other Spanish accents are all present: "menu[acute]", "Opcio[acute]n", "o[acute]rdenes", "cua[acute]les" (indirect
  interrogative, correctly accented), "envi[acute]a" (hiatus), "esta[acute]", "ma[acute]quina".
- French accents and agreement are all correct: "fac[cedilla]ons", "supprime[acute]", "touche[acute]",
  "re[acute]essayer", "comple[grave]te", "recommande[acute]e" (feminine, agrees with "option"), "a[grave] voix haute".
  Spanish "recomendada" likewise agrees with "opcio[acute]n".
- Product names are untouched in both languages: "le Cockpit" / "el Cockpit", "la session
  devthrottle" / "la sesio[acute]n devthrottle" (lowercase preserved).
- French colon spacing ("Option {0} : {1}.", "des ordres :", "Attention :") follows French
  typography. It is inaudible, so it costs nothing and is correct on the page.

---

## FRENCH

### FR-1 - car-mode.help-script - "faites-moi lire" is the wrong shape for a spoken command

Text: "faites-moi lire la session suivante"

The English item it renders is "read me the next one" - the literal sentence the user
utters. The French turns it into a causative ("have me read the next session"), which is
referentially defensible (the speaker is the assistant, so "moi" is the assistant) but
lands ambiguously when heard, because the listener's default reading of "faites-moi lire X"
is "make ME read X" - i.e. the driver does the reading. In a list whose other items are
plain commands ("reportez-la, approuvez-la ou supprimez-la"), this one item changes gear
and forces the listener to re-parse mid-sentence. Behind the wheel that is exactly the
sentence that gets lost.

Replacement: `lisez-moi la suivante`

Full clause: "Par de[acute]faut, vous me donnez des ordres : demandez qui a besoin de vous,
lisez-moi la suivante, reportez-la, approuvez-la ou supprimez-la."

### FR-2 - car-mode.help-script - "transmettez" does not mean "reply"

Text: "commencez par un de ces mots : dites, re[acute]pondez, transmettez ou envoyez"

English is "tell, answer, reply, or message". French has one verb for answer/reply, so the
translator needed a fourth word and reached for "transmettez" - which means forward or
relay, not reply. This is the one line in the whole script that teaches the user which
words to say. Teaching a word nobody would spontaneously say to a session wastes the slot.

Replacement: `commencez par un de ces mots : dites, re[acute]pondez, e[acute]crivez ou envoyez`

("e[acute]crivez" is what a developer actually says about pushing text into a session, and it is
short enough not to bloat an already long spoken paragraph.)

### FR-3 - waiting-screen.menu-narration-suffix - "by voice" is dropped, and two "a[grave]" phrases stack

Text: "Je ne peux pas encore re[acute]pondre a[grave] un menu a[grave] votre place."

Two problems in one sentence. First, accuracy: the English is "I can't answer that by
voice yet" - the limitation is the VOICE channel, not agency. The French says "I can't
answer a menu on your behalf yet", which is a different claim and also duplicates, almost
word for word, what waiting-screen.menu already says ("choisir une option a[grave] votre place").
The Spanish for this same entry keeps it correctly ("por voz"), so the two languages now
promise different things. Second, delivery: "re[acute]pondre a[grave] un menu a[grave] votre place" stacks two
"a[grave]" prepositional phrases back to back and reads as a stammer.

Replacement: `Je ne peux pas encore y re[acute]pondre par la voix.`

### FR-4 - voice-turn.blocked-menu - "a[grave] l'e[acute]cran de cette session"

Text: "Un menu s'affiche a[grave] l'e[acute]cran de cette session"

"a[grave] l'e[acute]cran" is idiomatic only when the screen is unpossessed ("ce qui s'affiche a[grave]
l'e[acute]cran"). Once you attach an owner, French takes "sur".

Replacement: `Un menu s'affiche sur l'e[acute]cran de cette session`

### FR-5 - car-mode.give-up - "re[acute]pondre a[grave] cela" is stiff and thin when heard

Text: "Je n'arrive pas a[grave] re[acute]pondre a[grave] cela pour le moment."

"a[grave] cela" is written-register; spoken, the vous-form equivalent of the English "that" is
either "a[grave] c[cedilla]a" (too casual against "Veuillez re[acute]essayer" in the next breath) or a named
object. As it stands the register wobbles inside a two-sentence string, and "a[grave] cela"
carries almost no stress, so the listener hears "je n'arrive pas a[grave] re[acute]pondre" and a mumble.

Replacement: `Je n'arrive pas a[grave] re[acute]pondre a[grave] votre demande pour le moment. Veuillez re[acute]essayer.`

### FR-6 - waiting-screen.menu - "je pourrai ensuite reprendre" is bare and drops "from here"

Text: "choisissez-en une ; je pourrai ensuite reprendre."

English closes with "then I can carry on from here" - the reassurance is that nothing is
lost. "reprendre" with no complement ends the sentence on an unstressed verb and leaves the
listener waiting for the object that never comes. (The Spanish "luego podre[acute] continuar" has
the same omission but does not have the dangling-verb problem, so it passes.)

Replacement: `choisissez-en une ; je pourrai ensuite reprendre a[grave] partir de la[grave].`

---

## SPANISH

### ES-1 - voice-turn.blocked-menu - the relative clause has three possible antecedents

Text: "Hay un menu[acute] en la pantalla de esta sesio[acute]n que no he podido leer con claridad"

On the page you resolve "que" to "menu[acute]" because you can see the sentence. Spoken, "que"
sits immediately after "sesio[acute]n" and one noun away from "pantalla", so the listener's first
parse is "the session I couldn't read clearly". Front the location and the relative clause
lands on its noun with no ambiguity at all.

Replacement: `En la pantalla de esta sesio[acute]n hay un menu[acute] que no he podido leer con claridad`

### ES-2 - voice-turn.blocked-menu - "contestarlo" collocates badly with "menu[acute]"

Text: "asi[acute] que no voy a contestarlo a ciegas"

"contestar" takes preguntas, llamadas, mensajes, correos - things addressed to you. A menu
is answered with "responder". "Contestar un menu[acute]" is the kind of near-miss collocation that
marks a sentence as translated rather than written.

Replacement: `asi[acute] que no voy a responderlo a ciegas`

### ES-3 - menu.answer-multiple vs car-mode.delete-cancelled/done - "done" has two Spanish words

Text: "Di cua[acute]les se aplican y luego di listo." against "Hecho. He eliminado {0}."

The user is taught to say "listo" for done, while the assistant itself says "Hecho" for
done. Two words for one concept in the same voice session invites the user to say the wrong
one, and the interpretation layer is the only thing catching it. Pick one. "Listo." is the
more natural spoken Spanish for a completed action anyway.

Replacement (change the assistant's word, keep the user's): car-mode.delete-done ES ->
`Listo. He eliminado {0}.`

### ES-4 - menu.answer-multiple - "cua[acute]les se aplican" is a calque

Text: "Di cua[acute]les se aplican y luego di listo."

"which ones apply" -> "cua[acute]les se aplican" is understandable but is not how a Spanish
speaker asks someone to pick from a list; "aplicarse" here reads as a literal carry-over.

Replacement: `Di cua[acute]les corresponden y luego di listo.`

(Lower severity than the others - it is comprehensible, just faintly translated-sounding.)

### ES-5 - car-mode.help-script - "pi[acute]deme que te lea la siguiente sesio[acute]n" breaks the list rhythm

Text: "pregunta quie[acute]n te necesita, pi[acute]deme que te lea la siguiente sesio[acute]n, apla[acute]zala,
aprue[acute]bala o elimi[acute]nala"

The English list is a rattle of short commands; item two here is a six-word subordinate
clause with a subjunctive in it, sitting between two-word imperatives. Heard aloud the
list loses its beat exactly where the user is trying to memorise it, and the item is also
the only one phrased as "ask me to do X" rather than as the command itself. Same defect as
FR-1, same fix.

Replacement: `le[acute]eme la siguiente`

Full clause: "Por defecto me das o[acute]rdenes: pregunta quie[acute]n te necesita, le[acute]eme la siguiente,
apla[acute]zala, aprue[acute]bala o elimi[acute]nala."

("le[acute]eme" is esdru[acute]jula - le[acute]-e-me - and must carry the accent as written.)

### ES-6 - car-mode.help-script - "transmite" does not mean "reply"

Text: "empieza por una de estas palabras: di, responde, transmite o envi[acute]a"

Same defect as FR-2. "transmitir" is to relay or broadcast, not to reply. It is the least
likely of the four words to ever leave a driver's mouth, so it is a wasted quarter of the
keyword list.

Replacement: `empieza por una de estas palabras: di, responde, escribe o envi[acute]a`

### ES-7 - waiting-screen.menu-narration-suffix - "Aviso:" reads as a form heading

Text: "Aviso: esta sesio[acute]n ahora esta[acute] esperando una respuesta a un menu[acute]"

"Heads up" is conversational; "Aviso" is signage - the word on a notice board. Spoken by a
voice that elsewhere says "Ahora mismo no consigo..." it is a register drop into
officialese at the very moment the user needs to be nudged, not notified.

Replacement: `Atencio[acute]n: esta sesio[acute]n ahora esta[acute] esperando una respuesta a un menu[acute]`

---

## CROSS-CUTTING

### X-1 - Spanish is consistently Peninsular; confirm that is the intended market

Every past-tense statement uses the present perfect for immediate past - "no he tocado",
"He eliminado", "no he podido" - alongside "Ahora mismo", "De acuerdo" and "Por defecto".
This is correct, natural, consistent Peninsular Spanish. It is not a mistake and I am not
asking for it to be changed blindly. But in most of Latin America the preterite carries
immediate past ("no toque[acute]", "elimine[acute]"), and the perfect there reads faintly formal or
foreign. This is a one-time locale decision, not twelve separate edits: if the target is
generic or Latin American Spanish, switch those three verbs to the preterite; if the target
is Spain, ship as is. Flagging it because it is invisible until a user in Bogota hears it.

No French equivalent problem - the French is neutral hexagonal French with nothing
regionally marked.

---

## Per-language verdict

**French: acceptable to ship out loud.** It does not read as machine output - the accents,
agreement and vous-register are uniform, the sentence rhythm is human, and "Ouvrez la
session pour voir ce qu'elle demande" is the kind of line a translation engine does not
produce. One item is a genuine must-fix before shipping, not polish: FR-3, where the
French drops "by voice" and promises something different from the English and from the
Spanish. FR-1 and FR-2 are strongly recommended because they sit in the help script, which
is the one string that teaches the user how to speak to the product. FR-4 through FR-6 are
polish.

**Spanish: acceptable to ship out loud.** Cleanest part of the packet - every accented
imperative is correct, which is the single thing most likely to be wrong in a spoken
Spanish string set and the one that would have produced audible mispronunciation. Nothing
here is a must-fix. ES-3 (two words for "done") is the one I would fix before release
because it affects what the user actually says; ES-1, ES-2 and ES-5 are worth taking
because they are the three places where the Spanish would sound translated rather than
written. ES-4 and ES-7 are optional.

Neither language reads as machine output. Both are ship-quality prose with a short fix
list, and only FR-3 is an accuracy defect rather than a matter of polish.
