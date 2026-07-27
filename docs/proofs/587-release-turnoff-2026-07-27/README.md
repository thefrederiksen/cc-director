# Proof - release turn-off list (issue devthrottle_internal#587)

Before/after evidence for the six small UX cleanups from the 2026-07-27 screen-by-screen
walk. "Before" images were captured on a slot Director / dev Gateway built from
origin/main (5429ccdf); "after" images from the same setup built from this branch.

## 1. View menu: "Gateway Connection (preview)..." removed

- Before: [view-menu-before.png](view-menu-before.png) - the View menu ends with the
  temporary preview entry.
- After: [view-menu-after.png](view-menu-after.png) - the menu is Status / Toggle Right
  Panel / Reset Terminal View, nothing else.

## 2. Explain button and ExplainDialog deleted

No screenshot possible: the button was permanently `IsVisible="False"`, so before and
after render identically. The proof is the code: `BtnExplain` is gone from
MainWindow.axaml, its click handler and compose-lock line are gone from
MainWindow.axaml.cs, and `Controls/ExplainDialog.axaml(.cs)` are deleted. Nothing else
referenced the dialog.

## 3. Gateway /stats page redirects to /your-throttle

- Before: [stats-page-before.png](stats-page-before.png) - the bare standalone dashboard
  served outside the Cockpit shell (no navigation, off-brand duplicate of Your Throttle);
  [stats-page-before-transcript.txt](stats-page-before-transcript.txt) shows the 200
  text/html answer.
- After: [stats-redirect-after.txt](stats-redirect-after.txt) - `GET /stats` answers
  302 with `Location: /your-throttle`; [stats-after.png](stats-after.png) shows the
  browser landing on the Cockpit route. `GET /stats/data` (the JSON feed the Cockpit and
  phone pages read) is unchanged and still serves.

## 4. Mobile /endword route removed

- Before: [mobile-endword-before.png](mobile-endword-before.png) - the End Word Test
  rig still routable on the phone.
- After: [mobile-endword-after.png](mobile-endword-after.png) - /mobile/endword no
  longer resolves to the test rig; the router's recovery screen answers instead. The
  Car Mode idle screen's link to the rig is removed with it.

## 5. Settings > Agents: "Detection wizard..." renamed "Setup wizard..."

- Before: [settings-agents-before.png](settings-agents-before.png)
- After: [settings-agents-after.png](settings-agents-after.png)

## 6. Window title drops the instance suffix for a single-instance install

- Before: [director-title-before.png](director-title-before.png) - title reads
  "Director -- SOREN_NORTH" (the seeded default instance name) even though it is the
  only instance. The owner's live install shows the same shape: "Director -- Leader".
- After: [director-title-after.png](director-title-after.png) - title reads
  "DevThrottle Director" with no suffix. The suffix still appears when more than one
  named instance is registered - that is its purpose.
