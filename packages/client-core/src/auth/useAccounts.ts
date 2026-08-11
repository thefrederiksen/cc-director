// React's view of the account store (devthrottle_internal #1509). Any component showing who is signed
// in - the account switcher, the mobile app bar, the Accounts panel - reads through this so an add,
// a rename, or a removal moves all of them at once.
import { useEffect, useState } from "react";
import { activeAccount, listAccounts, subscribeToAccounts, type StoredAccount } from "./accountStore";

export interface AccountsView {
  /** Every account enrolled on this browser, in the order they were added. */
  accounts: StoredAccount[];
  /** The one the app is authenticating as, or null when this browser has not enrolled. */
  active: StoredAccount | null;
  /** True when there is more than one, which is when a switcher is worth showing at all. */
  many: boolean;
}

function read(): AccountsView {
  const accounts = listAccounts();
  return { accounts, active: activeAccount(), many: accounts.length > 1 };
}

/** The current accounts, re-read whenever the store changes. */
export function useAccounts(): AccountsView {
  const [view, setView] = useState<AccountsView>(read);
  useEffect(() => subscribeToAccounts(() => setView(read())), []);
  return view;
}
