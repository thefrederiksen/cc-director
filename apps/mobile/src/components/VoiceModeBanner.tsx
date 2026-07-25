import { useVoiceModeAll } from "@devthrottle/client-core/voice/useVoiceModeAll";

// THE VOICE MODE BANNER (owner, 2026-07-24). While the fleet is in voice mode, this bar is on EVERY screen
// of the app and carries the switch that turns it off.
//
// It does two jobs, and the second is the important one.
//
// It says which state you are in. Voice mode used to be a state nothing displayed and nothing held - you
// worked it out by looking at the roster, or you did not know.
//
// And it puts the way out wherever you are. The only off switch used to be a checkbox on one tab of the
// roster - which, with auto-speak running, is the screen you are on for three seconds between sessions.
// Everywhere else, there was no way to stop. Making that window longer only made it a bigger window to
// catch. A control that is present on the session screen auto-speak just dropped you into is not a window
// at all, and that is the actual fix for "I try to stop it and it just keeps going".
//
// Deliberately NOT a confirmation dialog: this is the escape hatch, and an escape hatch that asks "are you
// sure?" while a voice is talking over you is not one. Turning voice mode back on is one tap on the roster.
export function VoiceModeBanner() {
  const voice = useVoiceModeAll();

  // Null means the first read has not landed. Render nothing rather than flash a banner that may be wrong -
  // a banner that appears and vanishes on every app open teaches you to ignore it.
  if (voice.enabled !== true) return null;

  return (
    <div className="voicemode-banner" role="status">
      <span className="voicemode-dot" aria-hidden="true" />
      <span className="voicemode-text">
        Voice mode is on
        <span className="voicemode-sub">Every session on the Gateway narrates its turns</span>
        {voice.error !== null && <span className="voicemode-error">{voice.error}</span>}
      </span>
      <button
        type="button"
        className="voicemode-off"
        onClick={() => void voice.set(false)}
        disabled={voice.busy}
      >
        {voice.busy ? "Turning off..." : "Turn off"}
      </button>
    </div>
  );
}
