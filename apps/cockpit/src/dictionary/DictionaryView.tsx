import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  getDictionary,
  saveDictionary,
  type Dictionary,
} from "@devthrottle/client-core/dictation/dictionaryClient";

// The dictation Dictionary editor (issue #977, epic #967) - the React port of the Blazor Cockpit
// Dictionary.razor (#183). The human edits the vocabulary chips biased into speech-to-text and the
// common-mistranscriptions term-to-variant map fed to the cleanup pass, with a dirty-state Save that
// PUTs the whole glossary and re-renders the returned dictionary. It reads and writes same-origin
// through the Gateway front door (client-core) - never a Director address.
//
// The Gateway is the single source of truth for this glossary: it is used by phone-recording
// transcription and by live dictation/Speak on every Director connected to this Gateway.
export function DictionaryView() {
  const [dict, setDict] = useState<Dictionary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveMsg, setSaveMsg] = useState("");

  const [newVocab, setNewVocab] = useState("");
  const [newTerm, setNewTerm] = useState("");
  // Per-term draft text for the "+ wrong spelling" inputs, keyed by term.
  const [variantDrafts, setVariantDrafts] = useState<Record<string, string>>({});

  const clearMsgTimer = useRef<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const d = await getDictionary();
        if (!cancelled) setDict(d);
      } catch {
        if (!cancelled) setError("Failed to load. Is the Gateway running?");
      }
    })();
    return () => {
      cancelled = true;
      if (clearMsgTimer.current !== null) window.clearTimeout(clearMsgTimer.current);
    };
  }, []);

  const markDirty = useCallback(() => {
    setDirty(true);
    setSaveMsg("");
  }, []);

  // ---- vocabulary ----
  const addVocab = () => {
    const v = newVocab.trim();
    setNewVocab("");
    if (v.length === 0 || dict === null) return;
    if (!dict.vocabulary.includes(v)) {
      setDict({ ...dict, vocabulary: [...dict.vocabulary, v] });
      markDirty();
    }
  };

  const removeVocab = (i: number) => {
    if (dict === null || i < 0 || i >= dict.vocabulary.length) return;
    setDict({ ...dict, vocabulary: dict.vocabulary.filter((_, idx) => idx !== i) });
    markDirty();
  };

  // ---- mistranscriptions ----
  const addTerm = () => {
    const name = newTerm.trim();
    setNewTerm("");
    if (name.length === 0 || dict === null) return;
    if (!(name in dict.commonMistranscriptions)) {
      setDict({
        ...dict,
        commonMistranscriptions: { ...dict.commonMistranscriptions, [name]: [] },
      });
      markDirty();
    }
  };

  const removeTerm = (term: string) => {
    if (dict === null) return;
    const next = { ...dict.commonMistranscriptions };
    delete next[term];
    setDict({ ...dict, commonMistranscriptions: next });
    setVariantDrafts((d) => {
      const copy = { ...d };
      delete copy[term];
      return copy;
    });
    markDirty();
  };

  const setVariantDraft = (term: string, value: string) =>
    setVariantDrafts((d) => ({ ...d, [term]: value }));

  const addVariant = (term: string) => {
    if (dict === null) return;
    const v = (variantDrafts[term] ?? "").trim();
    setVariantDrafts((d) => ({ ...d, [term]: "" }));
    if (v.length === 0) return;
    const variants = dict.commonMistranscriptions[term];
    if (variants === undefined || variants.includes(v)) return;
    setDict({
      ...dict,
      commonMistranscriptions: { ...dict.commonMistranscriptions, [term]: [...variants, v] },
    });
    markDirty();
  };

  const removeVariant = (term: string, vi: number) => {
    if (dict === null) return;
    const variants = dict.commonMistranscriptions[term];
    if (variants === undefined || vi < 0 || vi >= variants.length) return;
    setDict({
      ...dict,
      commonMistranscriptions: {
        ...dict.commonMistranscriptions,
        [term]: variants.filter((_, idx) => idx !== vi),
      },
    });
    markDirty();
  };

  // ---- save ----
  const save = async () => {
    if (dict === null || !dirty) return;
    setSaving(true);
    setSaveMsg("saving...");
    try {
      const fresh = await saveDictionary(dict);
      setDict(fresh);
      setDirty(false);
      setSaveMsg("Saved");
      setVariantDrafts({});
      if (clearMsgTimer.current !== null) window.clearTimeout(clearMsgTimer.current);
      clearMsgTimer.current = window.setTimeout(() => setSaveMsg((m) => (m === "Saved" ? "" : m)), 4000);
    } catch (err) {
      setSaveMsg(`save failed: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setSaving(false);
    }
  };

  const terms = dict === null ? [] : Object.keys(dict.commonMistranscriptions);

  return (
    <div className="dc-root">
      <div className="dc-wrap">
        <div className="dc-top">
          <h1>Dictionary</h1>
          <Link className="dc-back" to="/">
            &larr; Dashboard
          </Link>
        </div>
        <p className="dc-sub">
          The Gateway is the single source of truth for this glossary. It is used by{" "}
          <strong>phone-recording transcription</strong> and by{" "}
          <strong>live dictation/Speak on every Director</strong> connected to this Gateway. Edits apply
          on the next recording or dictation - no file copying.
        </p>

        {dict === null ? (
          <p className="dc-loading">{error ?? "Loading dictionary..."}</p>
        ) : (
          <>
            <div className="dc-saverow">
              <div />
              <div className="dc-saveactions">
                <span className="dc-savemsg">{saveMsg}</span>
                <button className="dc-save" onClick={() => void save()} disabled={!dirty || saving}>
                  Save
                </button>
              </div>
            </div>

            {/* ---- Vocabulary ---- */}
            <div className="dc-section">
              <h2>Vocabulary</h2>
              <p className="dc-hint">Terms biased into speech-to-text.</p>
              <div className="dc-chips">
                {dict.vocabulary.map((term, i) => (
                  <span className="dc-chip" key={`${term}/${i}`}>
                    {term}
                    <button className="dc-x" type="button" title="Remove" onClick={() => removeVocab(i)}>
                      x
                    </button>
                  </span>
                ))}
                <input
                  className="dc-addbox"
                  placeholder="+ add term"
                  value={newVocab}
                  onChange={(e) => setNewVocab(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") addVocab();
                  }}
                />
              </div>
            </div>

            {/* ---- Common mistranscriptions ---- */}
            <div className="dc-section">
              <h2>Common mistranscriptions</h2>
              <p className="dc-hint">
                Correct term &larr; wrong spellings seen in practice. Fed to the cleanup pass.
              </p>
              <div>
                {terms.length === 0 ? (
                  <p className="dc-empty">No correction patterns yet.</p>
                ) : (
                  terms.map((term) => (
                    <div className="dc-mrow" key={term}>
                      <div className="dc-mterm">
                        <button className="dc-x" type="button" title="Remove term" onClick={() => removeTerm(term)}>
                          x
                        </button>
                        <span className="dc-name">{term}</span>
                        <span className="dc-arrow">&larr;</span>
                      </div>
                      <div className="dc-mvariants">
                        <div className="dc-chips">
                          {dict.commonMistranscriptions[term].map((variant, vi) => (
                            <span className="dc-chip" key={`${variant}/${vi}`}>
                              {variant}
                              <button
                                className="dc-x"
                                type="button"
                                title="Remove"
                                onClick={() => removeVariant(term, vi)}
                              >
                                x
                              </button>
                            </span>
                          ))}
                          <input
                            className="dc-addbox dc-variant-add"
                            placeholder="+ wrong spelling"
                            value={variantDrafts[term] ?? ""}
                            onChange={(e) => setVariantDraft(term, e.target.value)}
                            onKeyDown={(e) => {
                              if (e.key === "Enter") addVariant(term);
                            }}
                          />
                        </div>
                      </div>
                    </div>
                  ))
                )}
              </div>
              <div className="dc-addterm-row">
                <input
                  className="dc-addbox dc-addterm"
                  placeholder="+ add a term to correct"
                  value={newTerm}
                  onChange={(e) => setNewTerm(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") addTerm();
                  }}
                />
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
