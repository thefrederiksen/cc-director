// The "who am I" chip (devthrottle_internal #1509) - the compact identity marker both shells put in
// reach of the work, as opposed to the full AccountsPanel which is a destination.
//
// It renders NOTHING until this browser holds more than one account. A single-login browser has no
// question to answer, and a chip that always says the same thing is noise you learn to stop reading -
// which is exactly the moment it stops protecting you. The chip exists for one specific mistake: with
// two fleets on one phone, a message typed into what looks like the work fleet and sent to the personal
// one. So it appears precisely when that mistake becomes possible.
import { useAccounts } from "./useAccounts";
import "./accounts.css";

export interface AccountSwitcherProps {
  /** Open the Accounts screen, where switching actually happens. Routed by the shell. */
  onOpen: () => void;
  /** Extra class from the shell, for its own placement (the app bar, the menu header). */
  className?: string;
}

export function AccountSwitcher({ onOpen, className }: AccountSwitcherProps) {
  const { active, many } = useAccounts();
  if (!many || !active) return null;

  return (
    <button
      type="button"
      className={`accts-chip${className ? ` ${className}` : ""}`}
      onClick={onOpen}
      aria-label={`Signed in as ${active.label}. Switch account.`}
    >
      <span className="accts-chip-label">{active.label}</span>
      <span className="accts-chip-caret" aria-hidden="true">v</span>
    </button>
  );
}
