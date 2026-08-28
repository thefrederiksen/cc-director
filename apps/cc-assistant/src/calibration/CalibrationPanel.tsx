import { useCallback, useEffect, useState } from "react";
import { holdScreenAwake, type ScreenWakeLock } from "../screenWakeLock";
import {
  calibrate,
  describeCombination,
  forget,
  loadStored,
  type Calibration,
  type Rung,
} from "./calibrate";

// What the device settled on, and what it rejected on the way there.
//
// The rejected rungs are shown rather than hidden on purpose. A tool that silently drops to a worse
// model is indistinguishable from one that is broken, and the first question anybody asks when the
// answers get worse is "what is it actually running?". This answers that without being asked.

export function CalibrationPanel() {
  const [calibration, setCalibration] = useState<Calibration | null>(null);
  const [running, setRunning] = useState(false);
  const [progress, setProgress] = useState("");
  const [notes, setNotes] = useState<string[]>([]);
  const [sent, setSent] = useState<string | null>(null);

  useEffect(() => {
    setCalibration(loadStored());
  }, []);

  const note = useCallback((message: string) => {
    setNotes((previous) => [message, ...previous].slice(0, 30));
  }, []);

  const run = useCallback(async () => {
    setRunning(true);
    setNotes([]);
    setSent(null);
    setCalibration(null);

    let wakeLock: ScreenWakeLock | null = null;
    try {
      wakeLock = await holdScreenAwake(note);
    } catch (error) {
      note(`${error instanceof Error ? error.message : String(error)} Keep the screen on, or this will stop part way.`);
    }

    try {
      const decided = await calibrate(import.meta.env.BASE_URL, {
        onRungStart(combination, index, total) {
          setProgress(`Trying ${index} of ${total}: ${describeCombination(combination)}`);
        },
        onRungDone(rung) {
          setCalibration((previous) => ({
            version: 1,
            decidedAt: new Date().toISOString(),
            userAgent: navigator.userAgent,
            chosen: null,
            tried: [...(previous?.tried ?? []), rung],
          }));
        },
        onNote: note,
      });
      setCalibration(decided);
      setProgress(decided.chosen === null ? "No configuration worked on this device." : "Done.");

      try {
        const response = await fetch(`${import.meta.env.BASE_URL}api/result`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ kind: "calibration", ...decided }),
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
      setProgress("Stopped on a failure.");
    } finally {
      await wakeLock?.release();
      setRunning(false);
    }
  }, [note]);

  const copy = useCallback(async () => {
    if (calibration === null) {
      return;
    }
    try {
      await navigator.clipboard.writeText(JSON.stringify(calibration, null, 2));
      note("Result copied to the clipboard.");
    } catch (error) {
      note(`Could not copy: ${error instanceof Error ? error.message : String(error)}`);
    }
  }, [calibration, note]);

  const startAgain = useCallback(() => {
    forget();
    setCalibration(null);
    setProgress("");
    setNotes([]);
    setSent(null);
  }, []);

  return (
    <section>
      <h2>What this device should run</h2>

      {calibration === null && !running ? (
        <p className="status">
          This device has not been measured yet. Calibration tries the best configuration first and
          stops at the first one that is fast enough, accurate enough, and does not cut sentences
          short. It happens once and the answer is remembered.
        </p>
      ) : null}

      {calibration?.chosen != null ? (
        <p className="verdict good">
          RUNNING: {describeCombination(calibration.chosen)}
        </p>
      ) : null}

      {calibration !== null && calibration.chosen === null && !running ? (
        <p className="verdict bad">
          NOTHING WORKED. Every configuration was too slow, too inaccurate, or cut sentences short on
          this device. It is not able to do this, and running the least bad one anyway would only
          waste your time.
        </p>
      ) : null}

      <div className="row" style={{ marginTop: 12 }}>
        <button className="go" onClick={() => void run()} disabled={running}>
          {running ? "Measuring..." : calibration === null ? "Calibrate this device" : "Measure again"}
        </button>
        {calibration !== null ? <button onClick={() => void copy()}>Copy result</button> : null}
        {calibration !== null && !running ? <button onClick={startAgain}>Forget</button> : null}
        <span className="status">{progress}</span>
      </div>

      {sent !== null ? <p className="status sent">Result sent at {sent}.</p> : null}

      {calibration !== null && calibration.tried.length > 0 ? (
        <table>
          <thead>
            <tr><th>Tried</th><th>Factor</th><th>Errors</th><th>Complete</th><th>Outcome</th></tr>
          </thead>
          <tbody>
            {calibration.tried.map((rung) => (
              <tr key={describeCombination(rung.combination)} className={rung.verdict.passed ? undefined : "slow"}>
                <td>{describeCombination(rung.combination)}</td>
                <td>{format(rung.verdict.meanRealTimeFactor)}</td>
                <td>{percent(rung.verdict.meanErrorRate)}</td>
                <td>{percent(rung.verdict.worstCompleteness)}</td>
                <td className="text">{outcomeOf(rung)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}

      {notes.length > 0 ? (
        <ul className="log quiet">{notes.map((n, i) => <li key={`${i}-${n}`}>{n}</li>)}</ul>
      ) : null}
    </section>
  );
}

function outcomeOf(rung: Rung): string {
  return rung.verdict.passed ? "Chosen." : rung.verdict.failures.join(" ");
}

function format(value: number | null): string {
  return value === null ? "-" : value.toFixed(2);
}

function percent(value: number | null): string {
  return value === null ? "-" : `${Math.round(value * 100)}%`;
}
