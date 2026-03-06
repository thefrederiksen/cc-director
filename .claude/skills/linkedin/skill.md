---
name: linkedin
description: Interact with LinkedIn via browser. Triggers on "/linkedin", "linkedin", "post on linkedin", "linkedin messages", "linkedin search".
---
# LinkedIn Connection

Load the managed navigation skill, then carry out the user's request using cc-browser commands.

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

## Skill Instructions
Read the full navigation skill before acting:
$( cc-browser skills show --connection linkedin )

Follow the skill's workflows, timing, delays, and rules exactly.

## When Finished
Close the browser when all LinkedIn work is complete:
```
cc-browser connections close linkedin
```
Do NOT leave the browser open after completing the task.

## Rules
- ALWAYS follow the timing/delay requirements from the managed skill
- ALWAYS check authentication state first
- Use `cc-browser snapshot --interactive` to discover current element refs
- If a selector fails, try the skill's alternative selectors or use snapshot discovery
- Use `cc-browser skills learn linkedin "description"` to persist any new patterns you discover

$ARGUMENTS
