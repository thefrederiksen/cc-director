import { AiTab } from "./AiTab";
import { CarModeTab } from "./CarModeTab";
import { NotificationsTab } from "./NotificationsTab";
import { TranscriptionTab } from "./TranscriptionTab";
import { visibleTabs, type TabId } from "./tabs";
import "./settings.css";

// The Settings tab strip and the panel it selects - the whole page body, shared by the Cockpit and the
// phone. Each shell supplies only its own frame around this: a page heading and left rail on the
// desktop, a back link and app bar on the phone.
//
// Why the switch lives here rather than in each app: the two surfaces have to offer the SAME settings.
// A tab list shared but a switch copied is a switch that grows a fifth branch on one surface only.

export interface SettingsTabStripProps {
  active: TabId;
  onSelect: (tab: TabId) => void;
}

export function SettingsTabStrip({ active, onSelect }: SettingsTabStripProps) {
  return (
    <div className="settings-tabs" role="tablist" aria-label="Settings sections">
      {visibleTabs().map((t) => (
        <button
          key={t.id}
          type="button"
          role="tab"
          aria-selected={active === t.id}
          className={active === t.id ? "settings-tab active" : "settings-tab"}
          onClick={() => onSelect(t.id)}
        >
          {t.label}
        </button>
      ))}
    </div>
  );
}

export interface SettingsTabPanelProps {
  tab: TabId;
  /** Routes that exist on the mounting surface only. Omitted means "this surface has no such page", and
   *  the line that would link to it is not rendered - never a link to a route that does not exist. */
  accountHref?: string;
  transcriptionHealthHref?: string;
}

export function SettingsTabPanel({ tab, accountHref, transcriptionHealthHref }: SettingsTabPanelProps) {
  switch (tab) {
    case "notifications":
      return <NotificationsTab />;
    case "ai":
      return <AiTab accountHref={accountHref} />;
    case "transcription":
      return <TranscriptionTab healthHref={transcriptionHealthHref} />;
    case "carmode":
      return <CarModeTab />;
  }
}
