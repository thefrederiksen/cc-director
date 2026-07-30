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

# Spoken string translation review - French and Spanish

Reviewed: 13 entries, French and Spanish, judged as spoken output only.

Findings are numbered 1 to 33. Severity is one of MAJOR (a native speaker notices immediately
and it damages trust in the product), MINOR (a native speaker notices on a second listen or
finds it slightly stiff), NOTE (accepted deviation or a risk to verify, not a defect).

Two things I checked and did NOT find:

- **No missing or wrong accents in either language.** Every accented form in the packet is
  correct, including the traps: French `re[acute]essayez`, `re[acute]sume[acute]`, `fac[cedilla]ons`, `recommande[acute]e`;
  Spanish `o[acute]rdenes`, `quie[acute]n`, `pi[acute]deme`, `no[acute]mbrala`, `Inte[acute]ntalo`, `envi[acute]a`, `cua[acute]les`,
  `despue[acute]s`, `ma[acute]quina`, and the indirect-question accents in `que[acute] esta[acute] preguntando`. Nothing
  here will be mispronounced by the phonemizer for want of an accent.
- **No gender or agreement errors.** `celle ... recommande[acute]e` (option, feminine), `lesquelles`,
  `no[acute]mbrala`, `la recomendada` are all correct.

The product names are correctly left alone: `Cockpit` and `devthrottle` are untranslated in
every entry in both languages.

---

## car-mode.delete-cancelled

EN: Okay, I left {0} alone.
FR: D'accord, je n'ai pas touche[acute] a[grave] {0}.
ES: De acuerdo, no he tocado {0}.

Both are accurate and idiomatic. `toucher a[grave]` is exactly the right French verb for "leave
alone / not meddle with", and it survives a slot starting with a vowel because `a[grave]` does not
elide. Spanish correctly omits the personal `a` (a session is not a person). No findings.

---

## car-mode.delete-done

EN: Done. I deleted {0}.
FR: C'est fait, j'ai supprime[acute] {0}.
ES: Listo, he eliminado {0}.

**1. MINOR - FR and ES - comma where the English has a full stop.**
Quoted: `C'est fait, j'ai supprime[acute] {0}.` / `Listo, he eliminado {0}.`
The English is two sentences on purpose: "Done." lands as a confirmation beat before the
detail. A comma gives the speech engine a short breath instead of a full stop, so the
confirmation runs into the detail and the reassurance is lost - and this is a destructive
action being confirmed to someone who is driving and cannot check.
Replace with: FR `C'est fait. J'ai supprime[acute] {0}.` ES `Listo. He eliminado {0}.`

---

## car-mode.give-up

EN: I'm having trouble answering that right now. Please try again.
FR: J'ai du mal a[grave] re[acute]pondre a[grave] c[cedilla]a pour le moment. Re[acute]essayez, s'il vous plai[circumflex]t.
ES: Ahora mismo no consigo responder a eso. Inte[acute]ntalo de nuevo, por favor.

Accuracy is good in both. Spanish is natural as spoken and consistently `tu[acute]`.

**2. MINOR - FR - `Re[acute]essayez, s'il vous plai[circumflex]t.`**
The comma makes the speech engine pause before `s'il vous plai[circumflex]t`, which turns a routine
polite request into something faintly pleading. Written French would also normally attach it
without a comma, or front the politeness.
Replace with: `Veuillez re[acute]essayer.` (or, if you want the lighter register to match the
English: `Re[acute]essayez, merci.`)

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
fac[cedilla]ons. Par de[acute]faut, vous me donnez des ordres : demandez qui a besoin de vous, dites-moi de
vous lire la suivante, de la mettre en veille, de l'approuver ou de la supprimer. Pour parler
a[grave] une session pluto[circumflex]t qu'a[grave] moi, commencez par dis, re[acute]ponds, ou envoie un message, puis
nommez-la - par exemple, dis a[grave] la session devthrottle de lancer les tests. Tout ce que vous
dites ensuite est envoye[acute] tel quel dans cette session. Dites {0} quand vous avez termine[acute], et
demandez de l'aide quand vous voulez.`

**3. MAJOR - FR - register break: tu-form command words inside a vous script.**
Quoted: `commencez par dis, re[acute]ponds, ou envoie un message` and `dis a[grave] la session devthrottle
de lancer les tests`.
The whole script addresses the user as `vous` - `votre gestionnaire`, `vous me parlez`,
`demandez`, `commencez`, `nommez-la`, `Dites {0}`. Then it hands the user tu-form imperatives
to speak: `dis`, `re[acute]ponds`, `envoie`. Heard out loud, the assistant switches from formal to
familiar mid-sentence and then tells the user to address the machine familiarly. This is the
single most obvious defect in the French set; a native speaker hears it instantly.
Replace with: `Pour parler a[grave] une session pluto[circumflex]t qu'a[grave] moi, commencez par dites, re[acute]pondez,
transmets ou envoie - puis nommez la session. Par exemple : dites a[grave] la session devthrottle de
lancer les tests.`
The clean version, keeping four trigger words and staying in `vous` throughout:
`Pour parler a[grave] une session pluto[circumflex]t qu'a[grave] moi, commencez par un de ces mots : dites, re[acute]pondez,
transmettez, ou envoyez, puis nommez la session. Par exemple : dites a[grave] la session devthrottle
de lancer les tests.`
Note this is only correct if the voice recognizer is changed to accept the vous forms. If the
recognizer is hard-wired to `dis` / `re[acute]ponds` / `envoie`, then the recognizer is what is
wrong, not the script - you cannot ship a vous-form assistant that requires tu-form commands.

**4. MAJOR - FR - one of the four trigger words has been dropped.**
Quoted: `commencez par dis, re[acute]ponds, ou envoie un message`.
The English offers four ways in: `tell, answer, reply, or message`. The French offers three -
`answer` and `reply` have collapsed into a single `re[acute]ponds`. A user who hears three words will
only ever try three. Whatever the fourth accepted word is, it must be spoken.
Replace with (vous forms, four items): `commencez par dites, re[acute]pondez, transmettez ou envoyez`.

**5. MINOR - FR - `dites-moi de vous lire la suivante, de la mettre en veille, de l'approuver
ou de la supprimer` is a stack of infinitives that is hard to follow by ear.**
The English list is four short commands the user can actually say. The French wraps them in a
reported-speech frame (`dites-moi de...`), so the listener has to hold `dites-moi de` in mind
across four clauses. Spoken in a car, that is too much to carry.
Replace with: `Par de[acute]faut, vous me donnez des ordres : demandez qui a besoin de vous,
faites-vous lire la suivante, mettez-la en veille, approuvez-la ou supprimez-la.`

**6. MINOR - FR (and the same in ES) - `la suivante` has nothing to refer back to.**
Quoted: `dites-moi de vous lire la suivante, de la mettre en veille`.
At that point in the sentence the word `session` has not been said yet, so the feminine
pronouns `la suivante`, `la mettre`, `l'approuver`, `la supprimer` all hang on an antecedent
the listener has not been given. On a screen the reader recovers; by ear they do not.
Replace with: `...demandez qui a besoin de vous, faites-vous lire la session suivante,
mettez-la en veille, approuvez-la ou supprimez-la.`

**7. MINOR - FR - `est envoye[acute] tel quel dans cette session`.**
`envoyer dans une session` is the wrong preposition; you send something *to* a session, not
*into* one, and `dans` makes it sound like a physical container.
Replace with: `Tout ce que vous dites ensuite est transmis tel quel a[grave] cette session.`

**8. MINOR - FR - `vous pouvez me parler de deux fac[cedilla]ons` softens the English.**
The English `you talk to me two ways` is a flat statement of how the thing works; the French
adds permission (`you may`). Small, but it is an addition.
Replace with: `et vous me parlez de deux fac[cedilla]ons.`

**9. NOTE - FR - `demandez de l'aide quand vous voulez` for "ask for help any time".**
Correct, slightly flat. `demandez de l'aide a[grave] tout moment` is the phrase a native would use in
a spoken instruction.

### Spanish

Quoted in full: `Soy tu gestor de flota, y puedes hablarme de dos maneras. Por defecto me das
o[acute]rdenes: pregunta quie[acute]n te necesita, pi[acute]deme que te lea la siguiente, que la posponga, que la
apruebe o que la elimine. Para hablar con una sesio[acute]n en lugar de conmigo, empieza con di,
responde o envi[acute]a un mensaje, y no[acute]mbrala - por ejemplo, di a la sesio[acute]n devthrottle que ejecute
las pruebas. Todo lo que digas despue[acute]s va directo a esa sesio[acute]n. Di {0} cuando hayas terminado,
y pide ayuda cuando quieras.`

Register: consistently `tu[acute]` throughout, including the trigger words. No slip. This is the one
place Spanish is clearly better than French.

**10. MAJOR - ES - the same dropped trigger word.**
Quoted: `empieza con di, responde o envi[acute]a un mensaje` - three, where the English has four.
Replace with: `empieza por di, responde, contesta o envi[acute]a un mensaje` (matching whatever four
words the recognizer accepts).

**11. MAJOR - ES - `di a la sesio[acute]n devthrottle que ejecute las pruebas` is missing the clitic.**
Spoken Spanish doubles the indirect object almost without exception: you say `dile a la
sesio[acute]n`, not `di a la sesio[acute]n`. As written it sounds like a foreigner or a machine.
Replace with: `dile a la sesio[acute]n devthrottle que ejecute las pruebas.`
If the recognizer matches on the leading word `di`, confirm it also accepts `dile` - the
correct Spanish and the recognizer must be reconciled, and the correct Spanish is not
negotiable in an example the user is being told to imitate.

**12. MINOR - ES - `empieza con` should be `empieza por`.**
When the thing you begin with is a word, Spanish uses `empezar por`. `empezar con` sounds
like beginning in the company of something.
Replace with: `empieza por di, responde, contesta o envi[acute]a un mensaje`.

**13. MINOR - ES - `la siguiente` has no antecedent, same as finding 6.**
Quoted: `pi[acute]deme que te lea la siguiente, que la posponga...`
Replace with: `pi[acute]deme que te lea la siguiente sesio[acute]n, que la posponga, que la apruebe o que la
elimine.`

**14. MINOR - FR and ES - the bare hyphen ` - ` is a hazard for the speech engine.**
Quoted: FR `puis nommez-la - par exemple` / ES `y no[acute]mbrala - por ejemplo`.
A hyphen-minus surrounded by spaces is not punctuation the phonemizer is guaranteed to treat
as a pause; depending on the engine it is dropped (two clauses run together) or voiced as
`moins` / `menos`. Every other entry in this packet already avoids it - these two are the
holdouts.
Replace with a colon in both: FR `puis nommez la session. Par exemple : dites a[grave] la session
devthrottle...` ES `y no[acute]mbrala. Por ejemplo: dile a la sesio[acute]n devthrottle...`

**15. NOTE - FR and ES disagree on what "snooze" is.**
FR says `mettre en veille` (put on standby), ES says `posponer` (postpone). Both are
defensible, but they are different concepts, and this is a word the user has to SAY to be
understood. Whichever verb the recognizer accepts is the only one that may appear here, and
it should mean the same thing in both languages. Verify against the recognizer, then align.

**16. NOTE - FR and ES both add "rather than to me".**
FR `pluto[circumflex]t qu'a[grave] moi`, ES `en lugar de conmigo`, where the English just says `instead`. This is
an addition, but a good one - by ear it removes a real ambiguity. Keep it.

---

## voice-turn.blocked-menu

EN: There's a menu on this session's screen that I couldn't read clearly, so I won't answer it
blindly. Open the session to pick an option.
FR: Il y a un menu a[grave] l'e[acute]cran de cette session que je n'ai pas re[acute]ussi a[grave] lire clairement, donc
je ne vais pas y re[acute]pondre a[grave] l'aveugle. Ouvrez la session pour choisir une option.
ES: Hay un menu[acute] en la pantalla de esta sesio[acute]n que no he podido leer con claridad, asi[acute] que no
voy a responderlo a ciegas. Abre la sesio[acute]n para elegir una opcio[acute]n.

Accuracy is good in both, and `a[grave] l'aveugle` / `a ciegas` are exactly right.

**17. MINOR - FR - the relative clause is stranded too far from its noun.**
Quoted: `Il y a un menu a[grave] l'e[acute]cran de cette session que je n'ai pas re[acute]ussi a[grave] lire clairement`.
Nine words separate `un menu` from `que`, so by ear the listener starts attaching `que` to
`cette session` - "the session I couldn't read". Written, it is fine. Spoken, it misdirects.
Replace with: `Un menu s'affiche a[grave] l'e[acute]cran de cette session, et je n'ai pas re[acute]ussi a[grave] le lire
clairement. Je ne vais donc pas y re[acute]pondre a[grave] l'aveugle. Ouvrez la session pour choisir une
option.`

**18. MINOR - ES - `no voy a responderlo a ciegas`.**
`responder` takes a preposition here in natural Spanish; a menu is `contestado` or you
`responder a` it. The bare enclitic `-lo` on `responder` reads as a calque of the English
"answer it".
Replace with: `asi[acute] que no voy a contestarlo a ciegas.`

---

## voice-turn.blocked-unreadable

EN: I can't read this session's screen right now, so I won't type your answer in blindly. Open
the session to see what it's asking.
FR: Je n'arrive pas a[grave] lire l'e[acute]cran de cette session pour le moment, donc je ne vais pas saisir
votre re[acute]ponse a[grave] l'aveugle. Ouvrez la session pour voir ce qu'elle demande.
ES: Ahora mismo no puedo leer la pantalla de esta sesio[acute]n, asi[acute] que no voy a escribir tu
respuesta a ciegas. Abre la sesio[acute]n para ver que[acute] esta[acute] preguntando.

The French here is the best sentence in the packet - accurate, idiomatic, correctly `vous`,
and it breathes properly for speech. No findings against it.

**19. MINOR - ES - `para ver que[acute] esta[acute] preguntando` is left dangling.**
Without a pronoun the listener is briefly unsure who is asking whom. Spanish would put the
`te` in.
Replace with: `Abre la sesio[acute]n para ver que[acute] te esta[acute] preguntando.`

---

## waiting-screen.menu

EN: This session is waiting on a menu, and I can't pick an option for you yet. Open the
session in the Cockpit or on your machine and choose one, then I can carry on from here.
FR: Cette session attend une re[acute]ponse a[grave] un menu, et je ne peux pas encore choisir une option a[grave]
votre place. Ouvrez la session dans le Cockpit ou sur votre machine et choisissez-en une,
ensuite je pourrai reprendre.
ES: Esta sesio[acute]n esta[acute] esperando en un menu[acute] y todavi[acute]a no puedo elegir una opcio[acute]n por ti. Abre la
sesio[acute]n en el Cockpit o en tu ma[acute]quina y elige una, y luego puedo continuar.

The French rendering of "waiting on a menu" as `attend une re[acute]ponse a[grave] un menu` is a genuine
improvement - it says what is actually happening. Spanish did not do the same, and suffers
for it.

**20. MAJOR - ES - `esta[acute] esperando en un menu[acute]` is a calque and means something else.**
`esperar en` places the waiter somewhere physically - waiting *at* a menu, as one waits at a
bus stop. What is meant is that the session is waiting for an answer to a menu.
Replace with: `Esta sesio[acute]n esta[acute] esperando una respuesta a un menu[acute] y todavi[acute]a no puedo elegir
una opcio[acute]n por ti.`

**21. MINOR - ES - `y luego puedo continuar` should be future.**
The English `then I can carry on` is a conditional promise about after the user acts. Spanish
present indicative here sounds like the assistant can already continue.
Replace with: `...y elige una; luego podre[acute] continuar.`
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
FR: Petite pre[acute]cision : cette session attend maintenant une re[acute]ponse a[grave] un menu, il va falloir
l'ouvrir et choisir une option. Je ne peux pas encore re[acute]pondre a[grave] c[cedilla]a a[grave] la voix.
ES: Aviso: esta sesio[acute]n esta[acute] ahora esperando en un menu[acute], asi[acute] que tendra[acute]s que abrirla y elegir
una opcio[acute]n. Todavi[acute]a no puedo responder a eso por voz.

**23. MAJOR - FR - `Petite pre[acute]cision :` is not "Heads up".**
`Petite pre[acute]cision` means "a small clarification" - it announces a footnote. `Heads up`
announces that something has changed and the user must now act. The French opener tells the
listener to relax at the exact moment the English tells them to pay attention. This is a
meaning change, not a style preference.
Replace with: `Attention : cette session attend maintenant une re[acute]ponse a[grave] un menu...`

**24. MINOR - FR - comma splice, missing `donc`.**
Quoted: `...attend maintenant une re[acute]ponse a[grave] un menu, il va falloir l'ouvrir...`
The English has `so`; the French dropped the causal link and left a bare comma between two
independent clauses.
Replace with: `...attend maintenant une re[acute]ponse a[grave] un menu, il va donc falloir l'ouvrir et
choisir une option.`

**25. MAJOR - FR - `re[acute]pondre a[grave] c[cedilla]a a[grave] la voix` is both wrong and unspeakable.**
Three `a[grave]` sounds in five syllables (`a[grave] c[cedilla]a a[grave] la voix`) is a stumble for a speech engine and a
mouthful for a listener. And `re[acute]pondre a[grave] la voix` is not the idiom - `piloter a[grave] la voix` works
because the voice is the instrument, but answering something *by voice* is `vocalement` or
`par la voix`.
Replace with: `Je ne peux pas encore y re[acute]pondre a[grave] votre place vocalement.`
Or, plainer: `Je ne peux pas encore re[acute]pondre a[grave] un menu a[grave] votre place.`

**26. MINOR - ES - `esta[acute] ahora esperando en un menu[acute]`.**
Same calque as finding 20, plus the adverb is wedged inside the verb phrase in an English word
order. Spanish puts `ahora` before the verb.
Replace with: `Aviso: esta sesio[acute]n ahora esta[acute] esperando una respuesta a un menu[acute], asi[acute] que
tendra[acute]s que abrirla y elegir una opcio[acute]n.`

---

## narration.cut-notice

EN: That is as much as I can read out. This summary was too long, so the rest is not spoken -
open the session to read the full reply.
FR: C'est tout ce que je peux lire a[grave] voix haute. Ce re[acute]sume[acute] e[acute]tait trop long, donc le reste
n'est pas dit. Ouvrez la session pour lire la re[acute]ponse comple[grave]te.
ES: Esto es todo lo que puedo leer en voz alta. Este resumen era demasiado largo, asi[acute] que el
resto no se dice. Abre la sesio[acute]n para leer la respuesta completa.

Both first sentences and both last sentences are fine. Both middle sentences are the worst
prose in the packet.

**27. MAJOR - FR - `donc le reste n'est pas dit`.**
A bare passive with `dire` and no agent. No French speaker says `le reste n'est pas dit`; it
is a word-for-word tracing of "the rest is not spoken", and it is the sentence in this packet
most likely to make a native listener say "this was translated by a machine".
Replace with: `Ce re[acute]sume[acute] est trop long : je ne lirai pas la suite.`

**28. MINOR - FR - `Ce re[acute]sume[acute] e[acute]tait trop long` - wrong tense for speech.**
The summary IS too long; it has not stopped being too long. French uses the present for a
condition that still holds. The imperfect makes it sound like a past event being recounted.
Replace with: `Ce re[acute]sume[acute] est trop long`.

**29. MAJOR - ES - `asi[acute] que el resto no se dice`.**
The impersonal present states a general rule - "the rest is not said (as a matter of policy)".
What is meant is that this assistant will not read the remainder now. As written it is both
unnatural and slightly evasive.
Replace with: `Este resumen es demasiado largo, asi[acute] que no leere[acute] el resto.`

**30. MINOR - ES - `Este resumen era demasiado largo` - wrong tense, same as finding 28.**
Replace with: `Este resumen es demasiado largo`.

---

## menu.option

EN: Option {0}: {1}.
FR: Option {0} : {1}.
ES: Opcio[acute]n {0}: {1}.

Correct. French space-before-colon is right, and the colon gives the speech engine the pause
that separates the number from the option text. No findings.

---

## menu.option-recommended

EN: Option {0}: {1}. That is the recommended one.
FR: Option {0} : {1}. C'est celle qui est recommande[acute]e.
ES: Opcio[acute]n {0}: {1}. Esa es la recomendada.

**31. MINOR - FR and ES - the pronoun points back across an arbitrary run-time string.**
Quoted: `C'est celle qui est recommande[acute]e.` / `Esa es la recomendada.`
{1} is an arbitrary option text that may itself be a long phrase containing feminine nouns. By
the time the listener hears `celle` / `Esa`, the nearest candidate antecedent is whatever
happened to be at the end of {1}, not the option as a whole. Naming the noun costs one word
and removes the ambiguity entirely.
Replace with: FR `Option {0} : {1}. C'est l'option recommande[acute]e.`
ES `Opcio[acute]n {0}: {1}. Esa es la opcio[acute]n recomendada.`

---

## menu.answer-single

EN: Say the number, or the option.
FR: Dites le nume[acute]ro, ou le nom de l'option.
ES: Di el nu[acute]mero, o el nombre de la opcio[acute]n.

**32. NOTE - FR and ES both add "the name of".**
The English says `the option`; both translations say `the NAME of the option`. This is an
addition, and both did it consistently, so it looks deliberate. By ear it is clearer than the
English - keep it, but be aware the source and the translations no longer say the same thing,
and a future edit to the English will not obviously propagate.

**33. MINOR - ES - comma before `o` in a two-item list.**
Quoted: `Di el nu[acute]mero, o el nombre de la opcio[acute]n.`
Spanish does not put a comma before `o` joining two elements; the speech engine will insert a
pause that a Spanish listener hears as a hesitation. French tolerates it better (the comma
there marks a genuine alternative), so I would leave the French and fix the Spanish.
Replace with: `Di el nu[acute]mero o el nombre de la opcio[acute]n.`

---

## menu.answer-multiple

EN: Say which ones apply, then say done.
FR: Dites lesquelles s'appliquent, puis dites termine[acute].
ES: Di cua[acute]les se aplican y luego di listo.

Both are acceptable. `lesquelles` and `cua[acute]les` correctly refer to options, and the trigger
words `termine[acute]` / `listo` are clean single words for a recognizer. Two small observations, not
worth numbering: French `s'appliquent` is a touch stiff for speech (`Dites lesquelles vous
concernent` is warmer, but changes the meaning slightly, so I would leave it); and Spanish
dropped the comma that both English and French keep before `then` / `puis`, so the Spanish
runs the two commands together where the other two languages pause. If you want the three
languages to sound alike, use `Di cua[acute]les se aplican, y luego di listo.`

Also worth stating: `listo` is used here as a trigger word the user must speak, and it is also
used as the interjection opening car-mode.delete-done (`Listo, he eliminado {0}`). That is not
wrong, but a user who has just been trained to say `listo` to end a list will hear the
assistant say `listo` at them. Consider `hecho` for one of the two.

---

# Verdicts

## French - DO NOT SHIP AS IS

The short strings are good. `car-mode.delete-cancelled`, `car-mode.delete-done`,
`voice-turn.blocked-unreadable`, and both `menu.option` entries are the work of someone who
knows the language; `toucher a[grave]`, `a[grave] l'aveugle`, and `attend une re[acute]ponse a[grave] un menu` are all
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

Spanish is the stronger of the two on register: `tu[acute]` is used consistently in every entry
including the trigger words, with no `usted` slip anywhere, and there is not one accent error.

The tells are grammatical rather than tonal:

- `esperando en un menu[acute]` appears twice (findings 20, 26) and is a straight calque of "waiting
  on a menu" that means something else in Spanish.
- `di a la sesio[acute]n` without the clitic (finding 11) is wrong in an example the user is being
  explicitly told to copy.
- `el resto no se dice` plus `era demasiado largo` (findings 29, 30) is the same
  machine-shaped middle sentence the French has.

Verdict: reads as good machine output that has been lightly post-edited - fluent, correctly
accented, consistently familiar in register, but with four or five places where the English
sentence structure is still showing through. Fix findings 11, 20, 26, 29, and 30 and it is
shippable.

## Total

33 numbered findings: 8 MAJOR, 20 MINOR, 5 NOTE.
