import { Link } from "react-router-dom";
import { TranscriptionTestPanel } from "@devthrottle/client-core/dictation/TranscriptionTestPanel";

// "Test transcription" on the phone: read a passage in one of eight languages and see how much of it
// came back. The whole screen is the shared panel, the same component and the same scoring the Cockpit
// mounts, so a phone and a desktop can never report different accuracy for the same recording.
//
// Its own route, beside Test microphone, because they answer different questions: one is about the
// audio going in, the other about the text coming out. Someone whose dictation is poor needs to know
// which of the two is at fault.
export function TranscriptionTest() {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Test transcription</h1>
      </header>
      <TranscriptionTestPanel />
    </div>
  );
}
