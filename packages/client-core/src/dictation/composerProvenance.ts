// WHICH CHARACTERS OF A BROWSER COMPOSER CAME FROM A MICROPHONE (source logging, owner's ruling 2026-09-05).
//
// A composer is a plain textarea: once a transcript is inserted, nothing remembers which characters it was.
// The desktop solved this with a character-range record beside its box (ruling R20, SpokenTurnRule.
// ComposerProvenance in Core); this is the same record for the Cockpit and the phone, with the same
// semantics, so a turn that mixes typing and speech says WHICH characters were spoken on both surfaces.
//
// RANGES, NOT STRINGS. With the same words typed and then dictated, a record that knew only the transcript's
// TEXT still called the box spoken after the spoken copy was deleted. A range knows which of the two
// occurrences was spoken. Every change to the text is applied to the spans as an edit: characters inserted
// before a span move it, characters after it leave it alone, and any change that touches a span's own
// characters forgets it - an edited transcript is typed.
//
// The spans are CLAIMS, not facts. They ride to the Gateway with the send, and the Gateway verifies each one
// against the transcript it registered before recording it (the characters named must BE that transcript).
// A claim this record gets wrong is dropped there, not believed.

/** One run of characters that came from a transcript: the text from `start` for `length` characters. */
export interface SpokenSpan {
  start: number;
  length: number;
  /** The Gateway's id for the transcript those characters came from, when the surface has one. */
  transcriptId?: string;
}

/** The largest edited region this will align character by character (about a million cells). Beyond it the
 * whole region is treated as one change, which can only forget a span, never invent one. */
const ALIGNMENT_CELLS = 1_000_000;

export class ComposerProvenance {
  private text = "";
  private spans: SpokenSpan[] = [];

  /** The composer text this record describes. */
  get currentText(): string {
    return this.text;
  }

  /** The spoken ranges still standing in the text, in text order. */
  get currentSpans(): readonly SpokenSpan[] {
    return this.spans;
  }

  /**
   * A transcript was inserted: the composer now holds `textNow`, and its characters from `at` for the
   * transcript's length ARE that transcript. Any change the composer made around the insertion (a separating
   * space) is applied to the earlier spans first. An `at` that does not hold the transcript is a caller
   * defect and is refused rather than recorded as a range that means nothing.
   */
  inserted(textNow: string, transcript: string, at: number, transcriptId?: string): void {
    if (transcript.length === 0) {
      this.textChanged(textNow);
      return;
    }
    if (at < 0 || at + transcript.length > textNow.length || textNow.slice(at, at + transcript.length) !== transcript) {
      throw new Error(
        `the composer text does not hold the transcript at ${at}: ${transcript.length} spoken characters were said to be inserted there`,
      );
    }
    this.textChanged(textNow);
    this.spans.push({ start: at, length: transcript.length, transcriptId });
    this.spans.sort((a, b) => a.start - b.start);
  }

  /**
   * The composer's text is now `text`. The change from the text last known is applied to the spans as an
   * edit: a span whose characters all survived moves to where they now stand; one whose characters were
   * removed, changed, or split by an insertion is forgotten.
   *
   * Where the change happened is found in this order. `caretAfter` - where the caret stands after the change,
   * when the caller knows it - decides when the text alone cannot: with the same words twice over, deleting
   * the FIRST copy and deleting the SECOND leave the identical text, and only the caret says which characters
   * went; it is used only when the region it names reproduces the new text from the old. Otherwise the change
   * is the region between the common prefix and the common suffix, and when both middles are non-empty they
   * are aligned character by character so surviving spoken characters are found wherever they now stand.
   */
  textChanged(text: string, caretAfter?: number): void {
    const now = text ?? "";
    const was = this.text;
    if (now === was) return;
    if (this.spans.length === 0) {
      this.text = now;
      return;
    }

    let prefix = 0;
    const max = Math.min(was.length, now.length);
    while (prefix < max && was[prefix] === now[prefix]) prefix++;
    let suffix = 0;
    while (suffix < max - prefix && was[was.length - 1 - suffix] === now[now.length - 1 - suffix]) suffix++;
    let oldRegionEnd = was.length - suffix;
    let newRegionEnd = now.length - suffix;
    const delta = now.length - was.length;

    if (caretAfter !== undefined) {
      if (delta < 0 && caretAfter >= 0 && caretAfter - delta <= was.length
        && was.slice(0, caretAfter) + was.slice(caretAfter - delta) === now) {
        prefix = caretAfter;
        oldRegionEnd = caretAfter - delta;
        newRegionEnd = caretAfter;
      } else if (delta > 0 && caretAfter - delta >= 0 && caretAfter <= now.length
        && now.slice(0, caretAfter - delta) + now.slice(caretAfter) === was) {
        prefix = caretAfter - delta;
        oldRegionEnd = caretAfter - delta;
        newRegionEnd = caretAfter;
      }
    }

    // Where every old character went: its index in the new text, or -1 when it is gone.
    const map = new Array<number>(was.length);
    for (let i = 0; i < prefix; i++) map[i] = i;
    for (let i = oldRegionEnd; i < was.length; i++) map[i] = i + delta;
    for (let i = prefix; i < oldRegionEnd; i++) map[i] = -1;
    const oldMiddle = oldRegionEnd - prefix;
    const newMiddle = newRegionEnd - prefix;
    if (oldMiddle > 0 && newMiddle > 0 && oldMiddle * newMiddle <= ALIGNMENT_CELLS) {
      align(was, now, prefix, oldMiddle, newMiddle, map);
    }

    const kept: SpokenSpan[] = [];
    for (const span of this.spans) {
      const start = map[span.start];
      let intact = start >= 0;
      for (let k = 1; intact && k < span.length; k++) intact = map[span.start + k] === start + k;
      if (intact) kept.push({ start, length: span.length, transcriptId: span.transcriptId });
    }
    this.spans = kept;
    this.text = now;
  }

  /** Put back a record saved for this text. A span outside the text is a corrupt record and is refused. */
  restore(text: string, spans: readonly SpokenSpan[]): void {
    const now = text ?? "";
    for (const span of spans) {
      if (span.start < 0 || span.length <= 0 || span.start + span.length > now.length) {
        throw new Error(`a spoken span ${span.start}+${span.length} lies outside a text of ${now.length} characters`);
      }
    }
    this.text = now;
    this.spans = [...spans].sort((a, b) => a.start - b.start);
  }

  /** The composer was sent or cleared: nothing dictated stands in it any more. */
  reset(): void {
    this.text = "";
    this.spans = [];
  }

  /**
   * The spans over the text as it is actually SENT. Both composers trim what they submit, so the record is
   * projected the same way here rather than left describing the untrimmed box: a span that survives the trim
   * moves with it, and one trimmed entirely away is dropped. Returns the trimmed text with its spans, so the
   * text sent and the claim made about it can never come from two different strings.
   */
  forSend(): { text: string; spans: SpokenSpan[] } {
    const whole = this.text;
    const trimmed = whole.trim();
    const lead = trimmed.length === 0 ? 0 : whole.indexOf(trimmed);
    const spans: SpokenSpan[] = [];
    for (const span of this.spans) {
      const start = Math.max(0, span.start - lead);
      const end = Math.min(trimmed.length, span.start + span.length - lead);
      if (end > start) spans.push({ start, length: end - start, transcriptId: span.transcriptId });
    }
    return { text: trimmed, spans };
  }

  /** True when the text is one spoken span and nothing but whitespace outside it - the one rule both surfaces
   * apply (ruling R20). Two spans are an earlier dictation ahead of this one; any other character is typing. */
  isWhollySpoken(): boolean {
    if (this.spans.length !== 1) return false;
    const span = this.spans[0];
    return this.text.slice(0, span.start).trim().length === 0
      && this.text.slice(span.start + span.length).trim().length === 0;
  }
}

/** The longest common subsequence of the two middles, so each old character that survived is mapped to where
 * it stands now. Earlier matches win a tie, which is the reading a person gives an edit as well. */
function align(was: string, now: string, offset: number, oldLen: number, newLen: number, map: number[]): void {
  const width = newLen + 1;
  const table = new Int32Array((oldLen + 1) * width);
  for (let i = oldLen - 1; i >= 0; i--) {
    for (let j = newLen - 1; j >= 0; j--) {
      table[i * width + j] = was[offset + i] === now[offset + j]
        ? table[(i + 1) * width + (j + 1)] + 1
        : Math.max(table[(i + 1) * width + j], table[i * width + (j + 1)]);
    }
  }
  let a = 0;
  let b = 0;
  while (a < oldLen && b < newLen) {
    if (was[offset + a] === now[offset + b]) {
      map[offset + a] = offset + b;
      a++;
      b++;
    } else if (table[(a + 1) * width + b] >= table[a * width + (b + 1)]) {
      a++;
    } else {
      b++;
    }
  }
}
