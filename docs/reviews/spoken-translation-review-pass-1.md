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

# Spoken string translation review - French and Spanish

Reviewed: 13 entries, French and Spanish, judged as spoken output only.

Findings are numbered 1 to 33. Severity is one of MAJOR (a native speaker notices immediately
and it damages trust in the product), MINOR (a native speaker notices on a second listen or
finds it slightly stiff), NOTE (accepted deviation or a risk to verify, not a defect).

Two things I checked and did NOT find:

- **No missing or wrong accents in either language.** Every accented form in the packet is
  correct, including the traps: French `réessayez`, `résumé`, `façons`, `recommandée`;
  Spanish `órdenes`, `quién`, `pídeme`, `nómbrala`, `Inténtalo`, `envía`, `cuáles`,
  `después`, `máquina`, and the indirect-question accents in `qué está preguntando`. Nothing
  here will be mispronounced by the phonemizer for want of an accent.
- **No gender or agreement errors.** `celle ... recommandée` (option, feminine), `lesquelles`,
  `nómbrala`, `la recomendada` are all correct.

The product names are correctly left alone: `Cockpit` and `devthrottle` are untranslated in
every entry in both languages.

---

## car-mode.delete-cancelled

EN: Okay, I left {0} alone.
FR: D'accord, je n'ai pas touché à {0}.
ES: De acuerdo, no he tocado {0}.

Both are accurate and idiomatic. `toucher à` is exactly the right French verb for "leave
alone / not meddle with", and it survives a slot starting with a vowel because `à` does not
elide. Spanish correctly omits the personal `a` (a session is not a person). No findings.

---

## car-mode.delete-done

EN: Done. I deleted {0}.
FR: C'est fait, j'ai supprimé {0}.
ES: Listo, he eliminado {0}.

**1. MINOR - FR and ES - comma where the English has a full stop.**
Quoted: `C'est fait, j'ai supprimé {0}.` / `Listo, he eliminado {0}.`
The English is two sentences on purpose: "Done." lands as a confirmation beat before the
detail. A comma gives the speech engine a short breath instead of a full stop, so the
confirmation runs into the detail and the reassurance is lost - and this is a destructive
action being confirmed to someone who is driving and cannot check.
Replace with: FR `C'est fait. J'ai supprimé {0}.` ES `Listo. He eliminado {0}.`

---

## car-mode.give-up

EN: I'm having trouble answering that right now. Please try again.
FR: J'ai du mal à répondre à ça pour le moment. Réessayez, s'il vous plaît.
ES: Ahora mismo no consigo responder a eso. Inténtalo de nuevo, por favor.

Accuracy is good in both. Spanish is natural as spoken and consistently `tú`.

**2. MINOR - FR - `Réessayez, s'il vous plaît.`**
The comma makes the speech engine pause before `s'il vous plaît`, which turns a routine
polite request into something faintly pleading. Written French would also normally attach it
without a comma, or front the politeness.
Replace with: `Veuillez réessayer.` (or, if you want the lighter register to match the
English: `Réessayez, merci.`)

---

## car-mode.help-script

This is the longest entry, the one a new user hears first, and it carries most of the damage
in this packet.

EN: I'm your fleet manager, and you talk to me two ways. By default you command me - ask who
needs you, read me the next one, snooze it, approve it, or remove it. To talk to a session
instead, start with tell, answer, reply, or message, and name it - like, tell the devthrottle
session to run the tests. Whatever you say after that goes straight into that session. Say
{0} when you're done, and ask for help any time.

### French

Quoted in full: `Je suis votre gestionnaire de flotte, et vous pouvez me parler de deux
façons. Par défaut, vous me donnez des ordres : demandez qui a besoin de vous, dites-moi de
vous lire la suivante, de la mettre en veille, de l'approuver ou de la supprimer. Pour parler
à une session plutôt qu'à moi, commencez par dis, réponds, ou envoie un message, puis
nommez-la - par exemple, dis à la session devthrottle de lancer les tests. Tout ce que vous
dites ensuite est envoyé tel quel dans cette session. Dites {0} quand vous avez terminé, et
demandez de l'aide quand vous voulez.`

**3. MAJOR - FR - register break: tu-form command words inside a vous script.**
Quoted: `commencez par dis, réponds, ou envoie un message` and `dis à la session devthrottle
de lancer les tests`.
The whole script addresses the user as `vous` - `votre gestionnaire`, `vous me parlez`,
`demandez`, `commencez`, `nommez-la`, `Dites {0}`. Then it hands the user tu-form imperatives
to speak: `dis`, `réponds`, `envoie`. Heard out loud, the assistant switches from formal to
familiar mid-sentence and then tells the user to address the machine familiarly. This is the
single most obvious defect in the French set; a native speaker hears it instantly.
Replace with: `Pour parler à une session plutôt qu'à moi, commencez par dites, répondez,
transmets ou envoie - puis nommez la session. Par exemple : dites à la session devthrottle de
lancer les tests.`
The clean version, keeping four trigger words and staying in `vous` throughout:
`Pour parler à une session plutôt qu'à moi, commencez par un de ces mots : dites, répondez,
transmettez, ou envoyez, puis nommez la session. Par exemple : dites à la session devthrottle
de lancer les tests.`
Note this is only correct if the voice recognizer is changed to accept the vous forms. If the
recognizer is hard-wired to `dis` / `réponds` / `envoie`, then the recognizer is what is
wrong, not the script - you cannot ship a vous-form assistant that requires tu-form commands.

**4. MAJOR - FR - one of the four trigger words has been dropped.**
Quoted: `commencez par dis, réponds, ou envoie un message`.
The English offers four ways in: `tell, answer, reply, or message`. The French offers three -
`answer` and `reply` have collapsed into a single `réponds`. A user who hears three words will
only ever try three. Whatever the fourth accepted word is, it must be spoken.
Replace with (vous forms, four items): `commencez par dites, répondez, transmettez ou envoyez`.

**5. MINOR - FR - `dites-moi de vous lire la suivante, de la mettre en veille, de l'approuver
ou de la supprimer` is a stack of infinitives that is hard to follow by ear.**
The English list is four short commands the user can actually say. The French wraps them in a
reported-speech frame (`dites-moi de...`), so the listener has to hold `dites-moi de` in mind
across four clauses. Spoken in a car, that is too much to carry.
Replace with: `Par défaut, vous me donnez des ordres : demandez qui a besoin de vous,
faites-vous lire la suivante, mettez-la en veille, approuvez-la ou supprimez-la.`

**6. MINOR - FR (and the same in ES) - `la suivante` has nothing to refer back to.**
Quoted: `dites-moi de vous lire la suivante, de la mettre en veille`.
At that point in the sentence the word `session` has not been said yet, so the feminine
pronouns `la suivante`, `la mettre`, `l'approuver`, `la supprimer` all hang on an antecedent
the listener has not been given. On a screen the reader recovers; by ear they do not.
Replace with: `...demandez qui a besoin de vous, faites-vous lire la session suivante,
mettez-la en veille, approuvez-la ou supprimez-la.`

**7. MINOR - FR - `est envoyé tel quel dans cette session`.**
`envoyer dans une session` is the wrong preposition; you send something *to* a session, not
*into* one, and `dans` makes it sound like a physical container.
Replace with: `Tout ce que vous dites ensuite est transmis tel quel à cette session.`

**8. MINOR - FR - `vous pouvez me parler de deux façons` softens the English.**
The English `you talk to me two ways` is a flat statement of how the thing works; the French
adds permission (`you may`). Small, but it is an addition.
Replace with: `et vous me parlez de deux façons.`

**9. NOTE - FR - `demandez de l'aide quand vous voulez` for "ask for help any time".**
Correct, slightly flat. `demandez de l'aide à tout moment` is the phrase a native would use in
a spoken instruction.

### Spanish

Quoted in full: `Soy tu gestor de flota, y puedes hablarme de dos maneras. Por defecto me das
órdenes: pregunta quién te necesita, pídeme que te lea la siguiente, que la posponga, que la
apruebe o que la elimine. Para hablar con una sesión en lugar de conmigo, empieza con di,
responde o envía un mensaje, y nómbrala - por ejemplo, di a la sesión devthrottle que ejecute
las pruebas. Todo lo que digas después va directo a esa sesión. Di {0} cuando hayas terminado,
y pide ayuda cuando quieras.`

Register: consistently `tú` throughout, including the trigger words. No slip. This is the one
place Spanish is clearly better than French.

**10. MAJOR - ES - the same dropped trigger word.**
Quoted: `empieza con di, responde o envía un mensaje` - three, where the English has four.
Replace with: `empieza por di, responde, contesta o envía un mensaje` (matching whatever four
words the recognizer accepts).

**11. MAJOR - ES - `di a la sesión devthrottle que ejecute las pruebas` is missing the clitic.**
Spoken Spanish doubles the indirect object almost without exception: you say `dile a la
sesión`, not `di a la sesión`. As written it sounds like a foreigner or a machine.
Replace with: `dile a la sesión devthrottle que ejecute las pruebas.`
If the recognizer matches on the leading word `di`, confirm it also accepts `dile` - the
correct Spanish and the recognizer must be reconciled, and the correct Spanish is not
negotiable in an example the user is being told to imitate.

**12. MINOR - ES - `empieza con` should be `empieza por`.**
When the thing you begin with is a word, Spanish uses `empezar por`. `empezar con` sounds
like beginning in the company of something.
Replace with: `empieza por di, responde, contesta o envía un mensaje`.

**13. MINOR - ES - `la siguiente` has no antecedent, same as finding 6.**
Quoted: `pídeme que te lea la siguiente, que la posponga...`
Replace with: `pídeme que te lea la siguiente sesión, que la posponga, que la apruebe o que la
elimine.`

**14. MINOR - FR and ES - the bare hyphen ` - ` is a hazard for the speech engine.**
Quoted: FR `puis nommez-la - par exemple` / ES `y nómbrala - por ejemplo`.
A hyphen-minus surrounded by spaces is not punctuation the phonemizer is guaranteed to treat
as a pause; depending on the engine it is dropped (two clauses run together) or voiced as
`moins` / `menos`. Every other entry in this packet already avoids it - these two are the
holdouts.
Replace with a colon in both: FR `puis nommez la session. Par exemple : dites à la session
devthrottle...` ES `y nómbrala. Por ejemplo: dile a la sesión devthrottle...`

**15. NOTE - FR and ES disagree on what "snooze" is.**
FR says `mettre en veille` (put on standby), ES says `posponer` (postpone). Both are
defensible, but they are different concepts, and this is a word the user has to SAY to be
understood. Whichever verb the recognizer accepts is the only one that may appear here, and
it should mean the same thing in both languages. Verify against the recognizer, then align.

**16. NOTE - FR and ES both add "rather than to me".**
FR `plutôt qu'à moi`, ES `en lugar de conmigo`, where the English just says `instead`. This is
an addition, but a good one - by ear it removes a real ambiguity. Keep it.

---

## voice-turn.blocked-menu

EN: There's a menu on this session's screen that I couldn't read clearly, so I won't answer it
blindly. Open the session to pick an option.
FR: Il y a un menu à l'écran de cette session que je n'ai pas réussi à lire clairement, donc
je ne vais pas y répondre à l'aveugle. Ouvrez la session pour choisir une option.
ES: Hay un menú en la pantalla de esta sesión que no he podido leer con claridad, así que no
voy a responderlo a ciegas. Abre la sesión para elegir una opción.

Accuracy is good in both, and `à l'aveugle` / `a ciegas` are exactly right.

**17. MINOR - FR - the relative clause is stranded too far from its noun.**
Quoted: `Il y a un menu à l'écran de cette session que je n'ai pas réussi à lire clairement`.
Nine words separate `un menu` from `que`, so by ear the listener starts attaching `que` to
`cette session` - "the session I couldn't read". Written, it is fine. Spoken, it misdirects.
Replace with: `Un menu s'affiche à l'écran de cette session, et je n'ai pas réussi à le lire
clairement. Je ne vais donc pas y répondre à l'aveugle. Ouvrez la session pour choisir une
option.`

**18. MINOR - ES - `no voy a responderlo a ciegas`.**
`responder` takes a preposition here in natural Spanish; a menu is `contestado` or you
`responder a` it. The bare enclitic `-lo` on `responder` reads as a calque of the English
"answer it".
Replace with: `así que no voy a contestarlo a ciegas.`

---

## voice-turn.blocked-unreadable

EN: I can't read this session's screen right now, so I won't type your answer in blindly. Open
the session to see what it's asking.
FR: Je n'arrive pas à lire l'écran de cette session pour le moment, donc je ne vais pas saisir
votre réponse à l'aveugle. Ouvrez la session pour voir ce qu'elle demande.
ES: Ahora mismo no puedo leer la pantalla de esta sesión, así que no voy a escribir tu
respuesta a ciegas. Abre la sesión para ver qué está preguntando.

The French here is the best sentence in the packet - accurate, idiomatic, correctly `vous`,
and it breathes properly for speech. No findings against it.

**19. MINOR - ES - `para ver qué está preguntando` is left dangling.**
Without a pronoun the listener is briefly unsure who is asking whom. Spanish would put the
`te` in.
Replace with: `Abre la sesión para ver qué te está preguntando.`

---

## waiting-screen.menu

EN: This session is waiting on a menu, and I can't pick an option for you yet. Open the
session in the Cockpit or on your machine and choose one, then I can carry on from here.
FR: Cette session attend une réponse à un menu, et je ne peux pas encore choisir une option à
votre place. Ouvrez la session dans le Cockpit ou sur votre machine et choisissez-en une,
ensuite je pourrai reprendre.
ES: Esta sesión está esperando en un menú y todavía no puedo elegir una opción por ti. Abre la
sesión en el Cockpit o en tu máquina y elige una, y luego puedo continuar.

The French rendering of "waiting on a menu" as `attend une réponse à un menu` is a genuine
improvement - it says what is actually happening. Spanish did not do the same, and suffers
for it.

**20. MAJOR - ES - `está esperando en un menú` is a calque and means something else.**
`esperar en` places the waiter somewhere physically - waiting *at* a menu, as one waits at a
bus stop. What is meant is that the session is waiting for an answer to a menu.
Replace with: `Esta sesión está esperando una respuesta a un menú y todavía no puedo elegir
una opción por ti.`

**21. MINOR - ES - `y luego puedo continuar` should be future.**
The English `then I can carry on` is a conditional promise about after the user acts. Spanish
present indicative here sounds like the assistant can already continue.
Replace with: `...y elige una; luego podré continuar.`
That also removes the stutter of `y elige una, y luego`, two `y` in five words.

**22. MINOR - FR - comma splice before `ensuite`.**
Quoted: `et choisissez-en une, ensuite je pourrai reprendre.`
Two independent clauses joined by a comma. Spoken, the engine gives a short breath and the
sentence sounds like it lost its footing.
Replace with: `Ouvrez la session dans le Cockpit ou sur votre machine et choisissez-en une ;
je pourrai ensuite reprendre.`

---

## waiting-screen.menu-narration-suffix

EN: Heads up - this session is now waiting on a menu, so you'll need to open it and pick an
option; I can't answer that by voice yet.
FR: Petite précision : cette session attend maintenant une réponse à un menu, il va falloir
l'ouvrir et choisir une option. Je ne peux pas encore répondre à ça à la voix.
ES: Aviso: esta sesión está ahora esperando en un menú, así que tendrás que abrirla y elegir
una opción. Todavía no puedo responder a eso por voz.

**23. MAJOR - FR - `Petite précision :` is not "Heads up".**
`Petite précision` means "a small clarification" - it announces a footnote. `Heads up`
announces that something has changed and the user must now act. The French opener tells the
listener to relax at the exact moment the English tells them to pay attention. This is a
meaning change, not a style preference.
Replace with: `Attention : cette session attend maintenant une réponse à un menu...`

**24. MINOR - FR - comma splice, missing `donc`.**
Quoted: `...attend maintenant une réponse à un menu, il va falloir l'ouvrir...`
The English has `so`; the French dropped the causal link and left a bare comma between two
independent clauses.
Replace with: `...attend maintenant une réponse à un menu, il va donc falloir l'ouvrir et
choisir une option.`

**25. MAJOR - FR - `répondre à ça à la voix` is both wrong and unspeakable.**
Three `à` sounds in five syllables (`à ça à la voix`) is a stumble for a speech engine and a
mouthful for a listener. And `répondre à la voix` is not the idiom - `piloter à la voix` works
because the voice is the instrument, but answering something *by voice* is `vocalement` or
`par la voix`.
Replace with: `Je ne peux pas encore y répondre à votre place vocalement.`
Or, plainer: `Je ne peux pas encore répondre à un menu à votre place.`

**26. MINOR - ES - `está ahora esperando en un menú`.**
Same calque as finding 20, plus the adverb is wedged inside the verb phrase in an English word
order. Spanish puts `ahora` before the verb.
Replace with: `Aviso: esta sesión ahora está esperando una respuesta a un menú, así que
tendrás que abrirla y elegir una opción.`

---

## narration.cut-notice

EN: That is as much as I can read out. This summary was too long, so the rest is not spoken -
open the session to read the full reply.
FR: C'est tout ce que je peux lire à voix haute. Ce résumé était trop long, donc le reste
n'est pas dit. Ouvrez la session pour lire la réponse complète.
ES: Esto es todo lo que puedo leer en voz alta. Este resumen era demasiado largo, así que el
resto no se dice. Abre la sesión para leer la respuesta completa.

Both first sentences and both last sentences are fine. Both middle sentences are the worst
prose in the packet.

**27. MAJOR - FR - `donc le reste n'est pas dit`.**
A bare passive with `dire` and no agent. No French speaker says `le reste n'est pas dit`; it
is a word-for-word tracing of "the rest is not spoken", and it is the sentence in this packet
most likely to make a native listener say "this was translated by a machine".
Replace with: `Ce résumé est trop long : je ne lirai pas la suite.`

**28. MINOR - FR - `Ce résumé était trop long` - wrong tense for speech.**
The summary IS too long; it has not stopped being too long. French uses the present for a
condition that still holds. The imperfect makes it sound like a past event being recounted.
Replace with: `Ce résumé est trop long`.

**29. MAJOR - ES - `así que el resto no se dice`.**
The impersonal present states a general rule - "the rest is not said (as a matter of policy)".
What is meant is that this assistant will not read the remainder now. As written it is both
unnatural and slightly evasive.
Replace with: `Este resumen es demasiado largo, así que no leeré el resto.`

**30. MINOR - ES - `Este resumen era demasiado largo` - wrong tense, same as finding 28.**
Replace with: `Este resumen es demasiado largo`.

---

## menu.option

EN: Option {0}: {1}.
FR: Option {0} : {1}.
ES: Opción {0}: {1}.

Correct. French space-before-colon is right, and the colon gives the speech engine the pause
that separates the number from the option text. No findings.

---

## menu.option-recommended

EN: Option {0}: {1}. That is the recommended one.
FR: Option {0} : {1}. C'est celle qui est recommandée.
ES: Opción {0}: {1}. Esa es la recomendada.

**31. MINOR - FR and ES - the pronoun points back across an arbitrary run-time string.**
Quoted: `C'est celle qui est recommandée.` / `Esa es la recomendada.`
{1} is an arbitrary option text that may itself be a long phrase containing feminine nouns. By
the time the listener hears `celle` / `Esa`, the nearest candidate antecedent is whatever
happened to be at the end of {1}, not the option as a whole. Naming the noun costs one word
and removes the ambiguity entirely.
Replace with: FR `Option {0} : {1}. C'est l'option recommandée.`
ES `Opción {0}: {1}. Esa es la opción recomendada.`

---

## menu.answer-single

EN: Say the number, or the option.
FR: Dites le numéro, ou le nom de l'option.
ES: Di el número, o el nombre de la opción.

**32. NOTE - FR and ES both add "the name of".**
The English says `the option`; both translations say `the NAME of the option`. This is an
addition, and both did it consistently, so it looks deliberate. By ear it is clearer than the
English - keep it, but be aware the source and the translations no longer say the same thing,
and a future edit to the English will not obviously propagate.

**33. MINOR - ES - comma before `o` in a two-item list.**
Quoted: `Di el número, o el nombre de la opción.`
Spanish does not put a comma before `o` joining two elements; the speech engine will insert a
pause that a Spanish listener hears as a hesitation. French tolerates it better (the comma
there marks a genuine alternative), so I would leave the French and fix the Spanish.
Replace with: `Di el número o el nombre de la opción.`

---

## menu.answer-multiple

EN: Say which ones apply, then say done.
FR: Dites lesquelles s'appliquent, puis dites terminé.
ES: Di cuáles se aplican y luego di listo.

Both are acceptable. `lesquelles` and `cuáles` correctly refer to options, and the trigger
words `terminé` / `listo` are clean single words for a recognizer. Two small observations, not
worth numbering: French `s'appliquent` is a touch stiff for speech (`Dites lesquelles vous
concernent` is warmer, but changes the meaning slightly, so I would leave it); and Spanish
dropped the comma that both English and French keep before `then` / `puis`, so the Spanish
runs the two commands together where the other two languages pause. If you want the three
languages to sound alike, use `Di cuáles se aplican, y luego di listo.`

Also worth stating: `listo` is used here as a trigger word the user must speak, and it is also
used as the interjection opening car-mode.delete-done (`Listo, he eliminado {0}`). That is not
wrong, but a user who has just been trained to say `listo` to end a list will hear the
assistant say `listo` at them. Consider `hecho` for one of the two.

---

# Verdicts

## French - DO NOT SHIP AS IS

The short strings are good. `car-mode.delete-cancelled`, `car-mode.delete-done`,
`voice-turn.blocked-unreadable`, and both `menu.option` entries are the work of someone who
knows the language; `toucher à`, `à l'aveugle`, and `attend une réponse à un menu` are all
choices a translation engine does not usually make on its own.

But four entries would not pass a native reviewer:

- `car-mode.help-script` mixes `vous` with tu-form commands (finding 3) and drops one of the
  four trigger words (finding 4). This is the first thing a new user hears, and the register
  break is audible within one sentence.
- `narration.cut-notice` contains `le reste n'est pas dit` (finding 27), which is plainly
  machine-shaped French.
- `waiting-screen.menu-narration-suffix` opens with the wrong signal word (finding 23) and
  ends with a phrase that is both unidiomatic and physically awkward to speak (finding 25).
- Three comma splices across the set (findings 22, 24, and the run-on in 17) each cost the
  speech engine a full stop it needs.

Verdict: reads as competent human French with a machine-translated help script bolted on.
Fix findings 3, 4, 23, 25, and 27 at minimum before this is heard by a French user.

## Spanish - CLOSE, BUT NOT YET

Spanish is the stronger of the two on register: `tú` is used consistently in every entry
including the trigger words, with no `usted` slip anywhere, and there is not one accent error.

The tells are grammatical rather than tonal:

- `esperando en un menú` appears twice (findings 20, 26) and is a straight calque of "waiting
  on a menu" that means something else in Spanish.
- `di a la sesión` without the clitic (finding 11) is wrong in an example the user is being
  explicitly told to copy.
- `el resto no se dice` plus `era demasiado largo` (findings 29, 30) is the same
  machine-shaped middle sentence the French has.

Verdict: reads as good machine output that has been lightly post-edited - fluent, correctly
accented, consistently familiar in register, but with four or five places where the English
sentence structure is still showing through. Fix findings 11, 20, 26, 29, and 30 and it is
shippable.

## Total

33 numbered findings: 8 MAJOR, 20 MINOR, 5 NOTE.
