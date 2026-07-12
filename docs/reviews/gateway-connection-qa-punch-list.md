# Gateway Connection - QA punch list

A running list of deferred cosmetic and quality items found during the mission, to be resolved by
the end and folded into the final verification report (definition-of-done item 5). Kept so nothing
is lost between phases. Append new items as they are found.

## Open

_(none)_

## Resolved

- **Step 1 Advanced disclosure trailing cell** (found Phase 1, Architect review of PR #1327). The
  collapsed "Enter the address manually" row used Avalonia's default `Expander` chrome, which left a
  thin trailing bordered cell on the right and put the caret mid-row, so the row did not span full
  width like the option rows above. Fix: replaced the `Expander` with a full-width `ToggleButton`
  disclosure (caret flush right, no stray cell). Written during Phase 1; lands and is
  screenshot-verified with the Phase 2 pull request.
