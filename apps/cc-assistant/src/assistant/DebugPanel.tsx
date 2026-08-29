// Everything Wilson knows and did, on one screen, clearly marked as the debug view.
//
// The production screen is one circle. This is the other screen: the turn log as the service kept
// it, the people and what is remembered about each (editable, because a memory you cannot correct is
// one you will not trust), the soul document (editable, versioned by the store), and voice enrolment.

import { useCallback, useEffect, useState } from "react";
import { ENROLMENT_LINES } from "./speakerId";

const BASE = import.meta.env.BASE_URL;

interface Person {
  readonly key: string;
  readonly name: string;
  readonly profile: Record<string, string>;
  readonly facts: ReadonlyArray<{ text: string; at: string; source: string }>;
  readonly voiceSamples: number;
}

interface TurnRecord {
  readonly at: string;
  readonly kind: string;
  readonly [field: string]: unknown;
}

export interface DebugPanelProps {
  /** Who this device says is talking, until the voice says otherwise. */
  readonly owner: string;
  readonly onOwnerChange: (name: string) => void;
  /** Record a few seconds and enrol it for a person. Provided by the screen, which owns the mic. */
  readonly enrol: (name: string, line: string) => Promise<string>;
  readonly speakerStatus: string;
  readonly lastIdentified: string;
  /** A counter the screen bumps after each turn so the log refreshes. */
  readonly version: number;
}

export function DebugPanel({ owner, onOwnerChange, enrol, speakerStatus, lastIdentified, version }: DebugPanelProps) {
  const [turns, setTurns] = useState<TurnRecord[]>([]);
  const [directory, setDirectory] = useState("");
  const [people, setPeople] = useState<Person[]>([]);
  const [places, setPlaces] = useState<Array<{ heard: string; name: string; admin1: string; country: string }>>([]);
  const [memoryNote, setMemoryNote] = useState("");
  const [soul, setSoul] = useState("");
  const [soulDraft, setSoulDraft] = useState("");
  const [soulNote, setSoulNote] = useState("");
  const [enrolNote, setEnrolNote] = useState("");
  const [enrolling, setEnrolling] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const t = (await (await fetch(`${BASE}api/turn?limit=60`)).json()) as { turns?: TurnRecord[]; directory?: string; reason?: string };
      setTurns((t.turns ?? []).slice().reverse());
      setDirectory(t.directory ?? t.reason ?? "");
      const p = (await (await fetch(`${BASE}api/people`)).json()) as { people?: Person[]; places?: Array<{ heard: string; name: string; admin1: string; country: string }>; reason?: string };
      setPeople(p.people ?? []);
      setPlaces(p.places ?? []);
      if (p.reason) {
        setMemoryNote(p.reason);
      }
    } catch (error) {
      setMemoryNote(`Could not read the service: ${error instanceof Error ? error.message : String(error)}`);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh, version]);

  useEffect(() => {
    void (async () => {
      try {
        const s = (await (await fetch(`${BASE}api/soul`)).json()) as { soul?: string | null; reason?: string };
        setSoul(s.soul ?? "");
        setSoulDraft(s.soul ?? "");
        if (s.reason) {
          setSoulNote(s.reason);
        }
      } catch {
        setSoulNote("Could not read the soul document.");
      }
    })();
  }, []);

  const saveSoul = async () => {
    try {
      const response = await fetch(`${BASE}api/soul`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ soul: soulDraft }) });
      const body = (await response.json()) as { soul?: string; error?: string };
      if (!response.ok) {
        setSoulNote(body.error ?? "Saving failed.");
        return;
      }
      setSoul(body.soul ?? soulDraft);
      setSoulNote("Saved. The next turn uses it.");
    } catch (error) {
      setSoulNote(`Saving failed: ${error instanceof Error ? error.message : String(error)}`);
    }
  };

  const editProfile = async (name: string, field: string, value: string) => {
    await fetch(`${BASE}api/people`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name, profile: { [field]: value } }) });
    await refresh();
  };

  const forget = async (name: string, text: string) => {
    await fetch(`${BASE}api/people`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name, forget: text }) });
    await refresh();
  };

  const forgetPlace = async (heard: string) => {
    await fetch(`${BASE}api/people`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ forgetPlace: heard }) });
    await refresh();
  };

  const clearLog = async () => {
    await fetch(`${BASE}api/turn`, { method: "DELETE" });
    await refresh();
  };

  const doEnrol = async (line: string) => {
    if (owner.trim().length === 0) {
      setEnrolNote("Say who you are first (the name box above).");
      return;
    }
    setEnrolling(true);
    setEnrolNote(`Read this out loud now: "${line}"`);
    try {
      setEnrolNote(await enrol(owner.trim(), line));
      await refresh();
    } catch (error) {
      setEnrolNote(`Enrolment failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      setEnrolling(false);
    }
  };

  return (
    <div className="debug">
      <div className="debugBanner">DEBUG VIEW. Nothing below is shown in the kitchen.</div>

      <section className="debugBlock">
        <h3>Who is talking</h3>
        <label>
          This device belongs to
          <input value={owner} onChange={(e) => onOwnerChange(e.target.value)} placeholder="Soren" spellCheck={false} />
        </label>
        <p className="debugLine">Voice model: {speakerStatus}. Last turn identified as: {lastIdentified}.</p>
        <p className="debugLine">Enrol your voice: press a line, then read it aloud. Three lines is enough. Do it once per person, with the name box set to that person.</p>
        <div className="debugRow">
          {ENROLMENT_LINES.map((line, i) => (
            <button key={line} disabled={enrolling} onClick={() => void doEnrol(line)}>
              Enrol line {i + 1}
            </button>
          ))}
        </div>
        {enrolNote.length > 0 ? <p className="debugLine strong">{enrolNote}</p> : null}
      </section>

      <section className="debugBlock">
        <h3>People and memory</h3>
        {memoryNote.length > 0 ? <p className="debugLine">{memoryNote}</p> : null}
        {people.length === 0 ? <p className="debugLine">Nobody yet. Wilson adds a person the first time a name talks to it.</p> : null}
        {people.map((p) => (
          <div key={p.key} className="person">
            <h4>
              {p.name} <span className="dim">({p.voiceSamples} voice samples)</span>
            </h4>
            <div className="profileGrid">
              {["callMe", "home", "currentLocation", "units", "household", "language"].map((field) => (
                <label key={field}>
                  {field}
                  <input
                    defaultValue={p.profile[field] ?? ""}
                    spellCheck={false}
                    onBlur={(e) => {
                      if ((p.profile[field] ?? "") !== e.target.value) {
                        void editProfile(p.name, field, e.target.value);
                      }
                    }}
                  />
                </label>
              ))}
            </div>
            {p.facts.length === 0 ? (
              <p className="debugLine">Nothing remembered yet.</p>
            ) : (
              <ul className="facts">
                {p.facts
                  .slice()
                  .reverse()
                  .map((f) => (
                    <li key={f.at + f.text}>
                      <span>{f.text}</span>
                      <span className="dim">{f.at.slice(0, 16).replace("T", " ")}</span>
                      <button onClick={() => void forget(p.name, f.text)}>forget</button>
                    </li>
                  ))}
              </ul>
            )}
          </div>
        ))}
      </section>

      <section className="debugBlock">
        <h3>Places, as heard and as resolved</h3>
        <p className="debugLine">A misheard name is corrected once and then remembered. If a correction is wrong, forget it here and it will be looked up fresh next time.</p>
        {places.length === 0 ? (
          <p className="debugLine">No places resolved yet.</p>
        ) : (
          <ul className="facts">
            {places.map((pl) => (
              <li key={pl.heard}>
                <span>
                  "{pl.heard}" is {pl.name}
                  {pl.admin1 ? `, ${pl.admin1}` : ""}
                  {pl.country ? `, ${pl.country}` : ""}
                </span>
                <button onClick={() => void forgetPlace(pl.heard)}>forget</button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="debugBlock">
        <h3>Soul document</h3>
        <p className="debugLine">Who Wilson is, in your words. Loaded into every turn. Previous versions are kept on disk.</p>
        <textarea value={soulDraft} onChange={(e) => setSoulDraft(e.target.value)} rows={10} spellCheck={false} />
        <div className="debugRow">
          <button onClick={() => void saveSoul()} disabled={soulDraft === soul}>
            Save soul
          </button>
          <button onClick={() => setSoulDraft(soul)} disabled={soulDraft === soul}>
            Revert
          </button>
          {soulNote.length > 0 ? <span className="debugLine">{soulNote}</span> : null}
        </div>
      </section>

      <section className="debugBlock">
        <h3>Turn log</h3>
        <p className="debugLine">
          {directory.length > 0 ? `Kept at ${directory}` : ""} Newest first. Server lines are what was heard and decided; "spoken" lines are what the page actually said.
        </p>
        <div className="debugRow">
          <button onClick={() => void refresh()}>Refresh</button>
          <button onClick={() => void clearLog()}>Clear log</button>
        </div>
        <ul className="log">
          {turns.map((t, i) => (
            <li key={i}>
              <span className="dim">{String(t.at).slice(11, 19)}</span>
              <span className={`kind kind-${t.kind}`}>{t.kind}</span>
              <span className="body">{describe(t)}</span>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}

/** One line per record, the fields that matter for that kind. */
function describe(t: TurnRecord): string {
  const parts: string[] = [];
  const pick = (...fields: string[]) => {
    for (const f of fields) {
      const v = t[f];
      if (v === undefined || v === null || v === "") {
        continue;
      }
      parts.push(`${f}=${typeof v === "string" ? v : JSON.stringify(v)}`);
    }
  };
  switch (t.kind) {
    case "turn":
      pick("speaker", "heard", "route", "actions", "query", "reply", "elapsedMs", "error");
      break;
    case "spoken":
      pick("said", "by", "firstSoundMs", "speechSeconds", "identified", "confidence", "suppressed");
      break;
    case "remembered":
      pick("speaker", "facts", "profile");
      break;
    case "weather":
      pick("asked", "place", "via", "notFound");
      break;
    default:
      pick(...Object.keys(t).filter((k) => k !== "at" && k !== "kind"));
  }
  return parts.join("  ");
}
