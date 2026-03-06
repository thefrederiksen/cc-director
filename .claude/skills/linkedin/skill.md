---
name: linkedin
description: Interact with LinkedIn via browser. Triggers on "/linkedin", "linkedin", "post on linkedin", "linkedin messages", "linkedin search".
---
# LinkedIn Connection

Load the navigation skill, then carry out the user's request using cc-browser commands.

## Setup - ALWAYS Start Fresh
ALWAYS close any existing LinkedIn browser before starting:
```
cc-browser connections close linkedin 2>&1 || true
```
Then open a fresh browser:
```
cc-browser connections open linkedin
```
Old browser windows carry stale state (open dialogs, previous message boxes, wrong pages).
Starting fresh prevents sending messages to the wrong person or with corrupted text.

When FINISHED with all LinkedIn work, close the browser:
```
cc-browser connections close linkedin
```
Do NOT leave the browser open after completing the task.

## Skill Instructions
Read the full navigation skill before acting:
$( cc-browser skills show --connection linkedin )

## Rules
- ALWAYS follow the timing/delay requirements from the skill
- ALWAYS check authentication state first
- Use `cc-browser snapshot --interactive` to discover current element refs
- If a selector fails, try the skill's alternative selectors or use snapshot discovery
- Use `cc-browser skills learn linkedin "description"` to persist any new patterns you discover

## Critical: JavaScript evaluate DOES NOT WORK on LinkedIn
LinkedIn's Content Security Policy blocks `unsafe-eval`. The `cc-browser evaluate` command
will ALWAYS fail on LinkedIn pages with a CSP error. NEVER use it.

Instead use:
- `cc-browser snapshot --interactive` to discover element refs
- `cc-browser click --ref <ref>` to click elements
- `cc-browser fill --ref <ref> --value "<text>"` to enter text
- `cc-browser screenshot` to visually verify state

## Critical: Text Input on LinkedIn (contentEditable)
LinkedIn message boxes and post editors use contentEditable divs, not regular inputs.

**ALWAYS use `fill`** for entering text into these fields. NEVER use `type`.

Why:
- `type` sends characters one-by-one. If it fails mid-stream (e.g. on special chars),
  partial text remains in the field with no way to undo it.
- `fill` first selects-all and deletes existing content, then inserts fresh text.
  It handles newlines properly using insertParagraph.
- If you call `type` then `fill`, you risk doubled/corrupted text because `type`
  may leave residual DOM nodes that `fill` cannot fully clear.

**Workflow for entering text:**
1. Click the text field ref to focus it
2. Use `fill --ref <ref> --value "<text>"` to set the text
3. Take a screenshot to verify the text looks correct
4. Only THEN click Send

**Newlines in messages:**
- `fill` handles `\n` in the value string -- splits on newlines and uses insertParagraph
- When passing multiline text via CLI, use a single-line string (newlines as literal \n)
  or let `fill` handle paragraph breaks

## Sending Messages Workflow

All cc-browser commands require `--connection linkedin` flag. Abbreviated below as `cc-browser -c linkedin`.

### Single Message Steps

1. Navigate to profile: `cc-browser -c linkedin navigate --url "https://www.linkedin.com/in/{username}"`
2. Wait 3s, then snapshot + grep for the SPECIFIC "Message {Name}" button:
   `cc-browser -c linkedin snapshot --interactive 2>&1 | grep -i "message"`
   Look for `button "Message {Name}" [ref=eXX]` -- this is the PROFILE's message button.
3. ALWAYS click the profile Message button (e.g. "Message Darren") to open a NEW conversation.
   NEVER reuse an already-open messaging overlay -- it may be for a different person.
4. Wait 2s, then snapshot + grep for "Write a message" textbox AND "Send" button:
   `cc-browser -c linkedin snapshot --interactive 2>&1 | grep -iE "write a message|Send"`
   Verify the conversation header shows the CORRECT recipient name.
5. Click textbox ref to focus it
6. Fill text: `cc-browser -c linkedin fill --ref <ref> --value "<message>"`
   The CLI converts literal \n to real newlines automatically.
7. Take screenshot to verify text is correct -- THIS IS MANDATORY before sending.
   Check: correct recipient, correct text, no literal \n characters, proper line breaks.
8. Click Send button ref (found in step 4, no need for another snapshot)
9. Wait 1s, take screenshot to confirm message was sent

### Verified Step-by-Step Process (do ONE step, screenshot, verify, then next)
1. Open fresh browser -> screenshot -> verify clean home page, no floating overlays
2. Navigate to profile -> screenshot -> verify correct person's profile loaded
3. Snapshot to find "Message {Name}" button -> click it
4. Screenshot -> verify message dialog opened for CORRECT person (check header name)
5. Snapshot to find textbox ref AND Send button ref (grep for both in one snapshot)
6. Click textbox -> fill message text
7. Screenshot -> MANDATORY verify: correct recipient, proper line breaks, no literal \n, correct content
8. Click Send
9. Screenshot -> verify message appears as sent (blue checkmarks, timestamp, empty textbox)
10. Mark as posted, close browser

### Critical Rules
- NEVER reuse an existing message dialog -- always start from a fresh browser
- LinkedIn messaging overlays persist across page navigation. Even after navigating to
  a new profile, the overlay may still show a DIFFERENT person's conversation.
  ALWAYS click the profile's "Message {Name}" button to open the correct conversation.
- After step 1, check for any floating message overlays on the home page. If present,
  close them before navigating to the profile.
- The deployed cc-browser is at %LOCALAPPDATA%/cc-director/bin/_cc-browser/src/cli.mjs.
  Edits to the repo source do NOT affect the deployed tool. Fix BOTH when making changes.
- The fill command converts literal \n to real newlines in the CLI layer.
  Always use \n in the --value string for line breaks (no special shell quoting needed).

### Batch Sending from Queue
When sending multiple messages from the queue:
1. Get all approved items: `cc-comm-queue list --status approved`
2. For each item, extract the recipient LinkedIn URL from the `destination` field
3. Close and reopen browser fresh for EACH message (prevents stale state contamination)
4. Send using the single message workflow above
5. Mark as posted immediately after each successful send (before moving to next)
6. If any send fails, fix the issue, then retry that message before continuing
7. Close browser when all messages are sent

## After Sending from Queue
Mark the item as posted: `cc-comm-queue mark-posted <id>`
(NOT `cc-comm-queue update` -- use the dedicated `mark-posted` command)

$ARGUMENTS
