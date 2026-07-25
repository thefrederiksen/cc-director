import { Link } from "react-router-dom";
import { MicTestPanel } from "@devthrottle/client-core/dictation/MicTestPanel";

// "Test microphone" on the phone. The whole screen is the shared MicTestPanel, which is the same
// component and the same measurements the Cockpit dictation health page mounts - a phone and a
// desktop must not disagree about whether a headset is any good.
//
// Its own route rather than a block inside AI settings: the check needs real vertical room once the
// playback player, the advice and the measurements are on screen, and a headset problem is worth
// reaching directly from the menu rather than hunting for inside another page.
export function MicTest() {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Test microphone</h1>
      </header>
      <MicTestPanel />
    </div>
  );
}
