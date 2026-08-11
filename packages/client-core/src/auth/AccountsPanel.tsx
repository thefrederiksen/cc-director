// The Accounts panel (devthrottle_internal #1507, #1509): who is signed in on this browser, switch
// between them, add another, and sign out. ONE component, mounted by both shells - the phone's Account
// screen and the desktop Cockpit's Account page - so the two surfaces cannot drift into two different
// products for one account (CLAUDE.md rule 8). Each shell supplies only its own frame around it.
//
// Signing out here is THIS BROWSER forgetting a key. It is deliberately not the same action as the
// Cockpit Account page's "Log out", which clears the GATEWAY's own link to a DevThrottle account
// (POST /account/logout) - a different thing that happens to a different machine. The two are worded
// apart on purpose; a shared word would make one button look like the other.
import { useState } from "react";
import { signOutAccount, signOutAllAccounts, switchAccount } from "./accountActions";
import { useAccounts } from "./useAccounts";
import "./accounts.css";

export interface AccountsPanelProps {
  /**
   * Start the add-an-account sign-in. The shells route to their own sign-in entry (the phone's
   * /mobile/signin, the Cockpit's /signin), which client-core must not hardcode for the other shell.
   */
  onAddAccount: () => void;
}

export function AccountsPanel({ onAddAccount }: AccountsPanelProps) {
  const { accounts, active, many } = useAccounts();
  // Which destructive action is awaiting confirmation, if any. Signing out costs a round trip through
  // devthrottle.com to undo, so it asks first - but only once it has been asked for, so the ordinary
  // screen is not carrying a warning nobody needed.
  const [confirming, setConfirming] = useState<"one" | "all" | null>(null);

  if (accounts.length === 0) {
    return (
      <div className="accts">
        <p className="accts-note">This browser is not signed in to any account.</p>
        <button type="button" className="accts-btn primary" onClick={onAddAccount}>
          Sign in
        </button>
      </div>
    );
  }

  const activeLabel = active?.label ?? "this account";

  return (
    <div className="accts">
      <div className="accts-list">
        {accounts.map((account) => {
          const isActive = account.id === active?.id;
          return (
            <button
              key={account.id}
              type="button"
              className={`accts-item${isActive ? " is-active" : ""}`}
              disabled={isActive}
              aria-current={isActive ? "true" : undefined}
              onClick={() => switchAccount(account.id)}
            >
              {/* An ASCII mark, not an icon font: the tick has to survive every terminal, log and
                  accessibility dump this app is read through. */}
              <span className="accts-mark" aria-hidden="true">{isActive ? "*" : ""}</span>
              <span className="accts-body">
                <span className="accts-label">{account.label}</span>
                <span className="accts-sub">
                  {isActive ? "Signed in now" : "Tap to switch"}
                  {account.email && account.email !== account.label ? ` - ${account.email}` : ""}
                </span>
              </span>
            </button>
          );
        })}
      </div>

      {many && (
        <p className="accts-note">
          Switching reloads the app as that account. Both stay signed in, so it needs no password and
          works with no connection.
        </p>
      )}

      {confirming === null && (
        <div className="accts-actions">
          <button type="button" className="accts-btn primary" onClick={onAddAccount}>
            Add account
          </button>
          <button type="button" className="accts-btn danger" onClick={() => setConfirming("one")}>
            Sign out of {activeLabel}
          </button>
          {many && (
            <button type="button" className="accts-btn danger" onClick={() => setConfirming("all")}>
              Sign out of all accounts
            </button>
          )}
        </div>
      )}

      {confirming !== null && (
        <div className="accts-confirm">
          <p>
            {confirming === "all"
              ? "Sign every account out of this browser? Signing back in means going through devthrottle.com again for each one."
              : many
                ? `Sign ${activeLabel} out of this browser? You stay signed in to your other account, and the app switches to it.`
                : `Sign ${activeLabel} out of this browser? You will need to sign in through devthrottle.com to use the app again.`}
          </p>
          <p className="accts-note">
            This browser forgets the key. The device stays on your account at devthrottle.com until you
            remove it there.
          </p>
          <div className="accts-confirm-row">
            <button
              type="button"
              className="accts-btn danger"
              onClick={() => {
                if (confirming === "all") signOutAllAccounts();
                else if (active) signOutAccount(active.id);
              }}
            >
              Sign out
            </button>
            <button type="button" className="accts-btn" onClick={() => setConfirming(null)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
