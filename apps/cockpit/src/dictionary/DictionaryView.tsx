import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useBlocker } from "react-router-dom";
import {
  getDictionary,
  saveDictionary,
  type Dictionary,
} from "@devthrottle/client-core/dictation/dictionaryClient";
import {
  addMistranscriptionTerm,
  addMistranscriptionVariant,
  addVocabularyWord,
} from "@devthrottle/client-core/dictation/dictionaryEdits";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { ConfirmDialog } from "../components";

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

  // Rejection notices shown next to each add box (issue #1255): a duplicate no longer silently clears
  // the input - it says so here, and the notice clears the moment the person edits that box again.
  const [vocabNotice, setVocabNotice] = useState("");
  const [termNotice, setTermNotice] = useState("");
  const [variantNotices, setVariantNotices] = useState<Record<string, string>>({});

  const clearMsgTimer = useRef<number | null>(null);

  // Guard against losing unsaved edits on navigation (issue #1255). useBlocker intercepts in-app route
  // changes (for example the "Sessions" back-link) while there are unsaved edits, so the person is asked
  // before the page unmounts and the edits vanish. The browser's own beforeunload covers a tab close or
  // refresh, which React Router cannot intercept.
  const blocker = useBlocker(dirty);
  // ConfirmDialog calls onClose right after a successful (synchronous) onConfirm. For the navigation
  // guard that would fire blocker.reset() immediately after blocker.proceed() and cancel the very
  // navigation we just allowed. This flag lets that trailing onClose no-op when we are proceeding, so
  // only a real cancel/dismiss resets the blocker.
  const proceedingRef = useRef(false);
  useEffect(() => {
    if (!dirty) return;
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // A non-empty returnValue is what triggers the browser's native "leave site?" prompt.
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", onBeforeUnload);
    return () => window.removeEventListener("beforeunload", onBeforeUnload);
  }, [dirty]);

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
    if (dict === null) return;
    const result = addVocabularyWord(dict, newVocab);
    if (result.status === "added") {
      setDict(result.dict);
      setNewVocab("");
      setVocabNotice("");
      markDirty();
    } else if (result.status === "duplicate") {
      // Keep the text so the person sees exactly what was rejected, and say why.
      setVocabNotice(`"${result.word}" is already in the vocabulary.`);
    } else {
      // Empty input - nothing to add and nothing to announce; just clear the box.
      setNewVocab("");
      setVocabNotice("");
    }
  };

  const removeVocab = (i: number) => {
    if (dict === null || i < 0 || i >= dict.vocabulary.length) return;
    setDict({ ...dict, vocabulary: dict.vocabulary.filter((_, idx) => idx !== i) });
    markDirty();
  };

  // ---- mistranscriptions ----
  const addTerm = () => {
    if (dict === null) return;
    const result = addMistranscriptionTerm(dict, newTerm);
    if (result.status === "added") {
      setDict(result.dict);
      setNewTerm("");
      setTermNotice("");
      markDirty();
    } else if (result.status === "duplicate") {
      setTermNotice(`"${result.word}" is already a term.`);
    } else {
      setNewTerm("");
      setTermNotice("");
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
    setVariantNotices((n) => {
      const copy = { ...n };
      delete copy[term];
      return copy;
    });
    markDirty();
  };

  const setVariantNotice = (term: string, value: string) =>
    setVariantNotices((n) => ({ ...n, [term]: value }));

  const setVariantDraft = (term: string, value: string) => {
    setVariantDrafts((d) => ({ ...d, [term]: value }));
    // Editing the box clears any stale duplicate notice for that term.
    setVariantNotices((n) => (n[term] ? { ...n, [term]: "" } : n));
  };

  const addVariant = (term: string) => {
    if (dict === null) return;
    const result = addMistranscriptionVariant(dict, term, variantDrafts[term] ?? "");
    if (result.status === "added") {
      setDict(result.dict);
      setVariantDrafts((d) => ({ ...d, [term]: "" }));
      setVariantNotice(term, "");
      markDirty();
    } else if (result.status === "duplicate") {
      setVariantNotice(term, `"${result.variant}" is already listed.`);
    } else {
      // Empty input or a term that vanished under the page - clear the box, nothing to announce.
      setVariantDrafts((d) => ({ ...d, [term]: "" }));
      setVariantNotice(term, "");
    }
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
      setSaveMsg(`save failed: ${gatewayErrorMessage(err)}`);
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
            &larr; Sessions
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
                  onChange={(e) => {
                    setNewVocab(e.target.value);
                    if (vocabNotice !== "") setVocabNotice("");
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") addVocab();
                  }}
                />
              </div>
              {vocabNotice !== "" && (
                <p className="dc-notice" role="status">
                  {vocabNotice}
                </p>
              )}
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
                        {(variantNotices[term] ?? "") !== "" && (
                          <p className="dc-notice" role="status">
                            {variantNotices[term]}
                          </p>
                        )}
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
                  onChange={(e) => {
                    setNewTerm(e.target.value);
                    if (termNotice !== "") setTermNotice("");
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") addTerm();
                  }}
                />
                {termNotice !== "" && (
                  <p className="dc-notice" role="status">
                    {termNotice}
                  </p>
                )}
              </div>
            </div>
          </>
        )}
      </div>

      {/* Warn before navigating away from unsaved dictionary edits (issue #1255). The blocker is armed
          only while `dirty` is true; confirming lets the pending navigation through, cancelling stays. */}
      <ConfirmDialog
        open={blocker.state === "blocked"}
        title="Leave with unsaved edits?"
        message="Your dictionary changes have not been saved. If you leave now they will be lost."
        confirmLabel="Leave without saving"
        cancelLabel="Stay on this page"
        onConfirm={() => {
          proceedingRef.current = true;
          blocker.proceed?.();
        }}
        onClose={() => {
          if (proceedingRef.current) {
            proceedingRef.current = false;
            return;
          }
          blocker.reset?.();
        }}
      />
    </div>
  );
}
