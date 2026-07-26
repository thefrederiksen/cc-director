import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { SettingsTabPanel, SettingsTabStrip } from "@devthrottle/client-core/settings/SettingsTabs";
import { tabFromParam, type TabId } from "@devthrottle/client-core/settings/tabs";

// The Cockpit Settings page (issue #1025, epic #967) - the React port of the retired Blazor
// wwwroot/pages/settings.html.
//
// The page BODY is now shared with the mobile app (client-core/settings): the same tabs, the same
// cards, one implementation. This file is only the desktop frame around it - the page heading, the
// lede, and the pointers to the sibling pages that own the settings which are not here.
//
// The tabs, and what belongs on each:
//
//   Notifications - how a session that needs you reaches you: snooze length, display time zone,
//                   notifications on this device
//   AI            - the hosted provider, the thinking models, the spoken language, the speech voice
//   Transcription - the transcription model, how your microphones are measuring, and the two on-demand
//                   checks (Test microphone, Test transcription)
//   Car Mode      - the phone's hands-free fleet control: model, end phrase, live phrase tester
//
// The Transcription tab is new here and it is the reason the two surfaces were unified: the checks used
// to live ONLY on the Transcription Health page on the desktop and on two standalone screens on the
// phone, so neither surface's Settings could answer "why is my dictation wrong?". The health page keeps
// the report over time - speed, failures, most-corrected words - and no longer duplicates the checks.
//
// Issue #2022 - self-host IS the hosted Gateway with one tenant, so this page is IDENTICAL on both
// surfaces: per-account, no surface branching. The machine settings left the web page (diagnostics to
// the About page, autostart to the installer + the `cc-devthrottle autostart` command, addressing
// dropped, brain removed), so there is no "This machine" tab.
//
// A pure client of existing Gateway endpoints, same-origin (root-relative URLs, never a Director
// address). Responsive (CodingStyle.md): each tab renders immediately with a loading line and loads
// asynchronously; on a failure it shows an explicit error banner, never a fabricated value.

export function SettingsView() {
  const [params] = useSearchParams();
  // The tab set is the same on both surfaces (issue #2022), so the initial tab is resolved straight from
  // ?tab= with no Gateway round-trip first - the page renders its tabs immediately, and each card loads its
  // own data and shows its own error banner.
  const [tab, setTab] = useState<TabId>(() => tabFromParam(params.get("tab")));

  return (
    <div className="page settings">
      <div className="page-head">
        <h1>Settings</h1>
      </div>
      <p className="settings-lede">Your DevThrottle account settings.</p>

      <p className="settings-relocated">
        Looking for something else? Your <Link to="/account">DevThrottle account</Link> and{" "}
        <Link to="/about">Gateway diagnostics</Link> each have their own page.
      </p>

      <SettingsTabStrip active={tab} onSelect={setTab} />

      <SettingsTabPanel tab={tab} accountHref="/account" transcriptionHealthHref="/transcription" />
    </div>
  );
}
