import React, { useEffect, useState } from "react";
import { onCreditsNeeded, type HostedAiUnavailable } from "@devthrottle/client-core/api/client";

// The ONE app-level out-of-credits notice for the whole PWA (issue #942, epic #937). Whenever any
// hosted-AI call returns HTTP 402 (out of credits, monthly cap, or - in bring-your-own mode - no key),
// the client emits the shared body and this shows the single-source message plus a call-to-action that
// deep-links to Billing. Mounted once at the app root, so every voice/Wingman/text-to-speech surface is
// covered without each one carrying its own credits UX.
//
// Add-$5-without-reload: it clears when the app returns to the foreground (visibilitychange), so after a
// top-up the next roster/voice poll re-checks and, if funded, produces no new 402 - the notice stays
// gone with no app reload. A stale notice is also dismissible.
export function CreditsNotice(): React.ReactElement | null {
  const [info, setInfo] = useState<HostedAiUnavailable | null>(null);

  // Subscribe to the client-core 402 emitter for the component's lifetime.
  useEffect(() => onCreditsNeeded(setInfo), []);

  // Returning to the foreground (e.g. back from the Billing tab) clears the notice so the next poll
  // re-checks against the new balance.
  useEffect(() => {
    function onVisibility(): void {
      if (document.visibilityState === "visible") setInfo(null);
    }
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, []);

  if (info === null) return null;

  return (
    <div className="credits-notice">
      <div className="banner banner-info banner-action">
        <span>{info.text}</span>
        <span className="credits-notice-actions">
          {/* The button renders only when the Gateway sent BOTH a destination and a label - the client
              never invents a call-to-action, and never defaults to a credits one (issue #1360). */}
          {info.ctaUrl !== null && info.ctaUrl !== "" && info.ctaLabel !== "" && (
            <a className="banner-btn" href={info.ctaUrl} target="_blank" rel="noopener noreferrer">
              {info.ctaLabel}
            </a>
          )}
          <button className="banner-btn credits-notice-dismiss" onClick={() => setInfo(null)}>
            Dismiss
          </button>
        </span>
      </div>
    </div>
  );
}
