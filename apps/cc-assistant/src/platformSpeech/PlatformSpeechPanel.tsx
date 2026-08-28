import { useCallback, useEffect, useState } from "react";
import {
  installLanguage,
  probePlatformSpeech,
  summarise,
  type PlatformSpeechProbe,
} from "./probe";

// The question that decides how much of this application needs to exist.
//
// If the platform will recognise speech on the device, for free, without a download, then it should
// do the all-day listening and Whisper only has to transcribe the command once the wake word has
// fired. If it will not, the model does both jobs and we carry eighty megabytes to every device.
//
// One thing the probe cannot answer, and it is worth saying out loud: the platform recogniser only
// accepts a live microphone, so it cannot be run against the fixed clips the way Whisper can. Its
// accuracy can only ever be judged live, by a person. That is a real cost of using it, and it is why
// the live check below exists at all.

export function PlatformSpeechPanel() {
  const [probe, setProbe] = useState<PlatformSpeechProbe | null>(null);
  const [busy, setBusy] = useState(false);
  const [notes, setNotes] = useState<string[]>([]);
  const [sent, setSent] = useState<string | null>(null);

  const note = useCallback((message: string) => {
    setNotes((previous) => [message, ...previous].slice(0, 20));
  }, []);

  const run = useCallback(async () => {
    setBusy(true);
    setSent(null);
    try {
      const result = await probePlatformSpeech();
      setProbe(result);
      try {
        const response = await fetch(`${import.meta.env.BASE_URL}api/result`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ kind: "platform-speech", ...result }),
        });
        if (response.ok) {
          const body = (await response.json()) as { receivedAt?: string };
          setSent(body.receivedAt ?? "sent");
        }
      } catch {
        note("The result could not be sent. Use Copy result instead.");
      }
    } catch (error) {
      note(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }, [note]);

  // Asked automatically, because it costs nothing: no microphone, no permission, no download.
  useEffect(() => {
    void run();
  }, [run]);

  const install = useCallback(
    async (language: string) => {
      setBusy(true);
      try {
        const ok = await installLanguage(language);
        note(ok ? `${language} installed. Checking again.` : `${language} could not be installed.`);
        await run();
      } catch (error) {
        note(error instanceof Error ? error.message : String(error));
      } finally {
        setBusy(false);
      }
    },
    [note, run],
  );

  const copy = useCallback(async () => {
    if (probe === null) {
      return;
    }
    try {
      await navigator.clipboard.writeText(JSON.stringify(probe, null, 2));
      note("Result copied to the clipboard.");
    } catch (error) {
      note(`Could not copy: ${error instanceof Error ? error.message : String(error)}`);
    }
  }, [note, probe]);

  const verdict = probe === null ? null : summarise(probe);
  const good = probe !== null && probe.languages.some((l) => l.onDevice === "available");

  return (
    <section>
      <h2>Can the phone do the listening itself?</h2>
      <p className="status">
        Whether this device can recognise speech on-device, privately, with nothing to download. If it
        can, the platform does the all-day listening and a model is only needed for the command.
      </p>

      {verdict !== null ? (
        <p className={good ? "verdict good" : "verdict warn"}>{verdict}</p>
      ) : (
        <p className="status">Checking...</p>
      )}

      {probe !== null ? (
        <>
          <dl className="readout">
            <dt>Recogniser present</dt>
            <dd>{probe.hasRecogniser ? (probe.prefixed ? "Yes, the older prefixed one" : "Yes") : "No"}</dd>
            <dt>Can be asked what it supports</dt>
            <dd>{probe.hasAvailabilityQuery ? "Yes, so it is Chrome 139 or later" : "No, so it predates on-device support"}</dd>
            <dt>Accepts local-only processing</dt>
            <dd>{probe.acceptsProcessLocally ? "Yes" : "No"}</dd>
          </dl>

          <table>
            <thead>
              <tr><th>Language</th><th>On this device</th><th>Anywhere</th><th /></tr>
            </thead>
            <tbody>
              {probe.languages.map((l) => (
                <tr key={l.language} className={l.onDevice === "available" ? undefined : "slow"}>
                  <td>{l.language}</td>
                  <td>{l.onDevice}</td>
                  <td>{l.anywhere}</td>
                  <td>
                    {l.onDevice === "downloadable" ? (
                      <button onClick={() => void install(l.language)} disabled={busy}>Install</button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : null}

      <div className="row" style={{ marginTop: 12 }}>
        <button onClick={() => void run()} disabled={busy}>{busy ? "Checking..." : "Check again"}</button>
        {probe !== null ? <button onClick={() => void copy()}>Copy result</button> : null}
      </div>

      {sent !== null ? <p className="status sent">Result sent at {sent}.</p> : null}

      {notes.length > 0 ? (
        <ul className="log quiet">{notes.map((n, i) => <li key={`${i}-${n}`}>{n}</li>)}</ul>
      ) : null}
    </section>
  );
}
