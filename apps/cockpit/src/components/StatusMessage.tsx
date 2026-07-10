import type { Flash } from "./useFlash";

// The presentational half of the transient-status helper (issue #1244): it renders a single Flash from
// useFlash as a small coloured status line. It renders nothing when there is no message, so a page can
// mount it unconditionally. An error flash announces itself to a screen reader; info/success are
// polite. This replaces the window.alert pop-ups that used to report action results.

export interface StatusMessageProps {
  /** The flash to render (from useFlash), or null to render nothing. */
  flash: Flash | null;
}

export function StatusMessage({ flash }: StatusMessageProps) {
  if (flash === null) return null;
  return (
    <span
      className={`ui-status ui-status-${flash.kind}`}
      role={flash.kind === "error" ? "alert" : "status"}
    >
      {flash.text}
    </span>
  );
}
