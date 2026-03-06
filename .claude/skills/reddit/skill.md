---
name: reddit
description: Interact with Reddit via browser. Triggers on "/reddit", "reddit", "post on reddit", "reddit messages", "reddit search".
---
# Reddit Connection

Load the navigation skill, then carry out the user's request using cc-browser commands.

IMPORTANT: Prefer cc-reddit CLI for Reddit operations (has human-like delays built in).
Only use cc-browser directly when cc-reddit lacks the needed functionality.

## Setup
Ensure browser is open: `cc-browser connections open reddit`

## Skill Instructions
Read the full navigation skill before acting:
$( cc-browser skills show --connection reddit )

## Rules
- ALWAYS follow the timing/delay requirements from the skill
- ALWAYS check authentication state first
- Use `cc-browser snapshot --interactive` to discover current element refs
- If a selector fails, try the skill's alternative selectors or use snapshot discovery
- Use `cc-browser skills learn reddit "description"` to persist any new patterns you discover

$ARGUMENTS
