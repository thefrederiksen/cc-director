---
name: spotify
description: Interact with Spotify via browser. Triggers on "/spotify", "spotify", "play music", "spotify playlist", "spotify search".
---
# Spotify Connection

Load the navigation skill, then carry out the user's request using cc-browser commands.

## Setup
Ensure browser is open: `cc-browser connections open spotify`

## Skill Instructions
Read the full navigation skill before acting:
$( cc-browser skills show --connection spotify )

## Rules
- ALWAYS follow the timing/delay requirements from the skill
- ALWAYS check authentication state first
- Use `cc-browser snapshot --interactive` to discover current element refs
- If a selector fails, try the skill's alternative selectors or use snapshot discovery
- Use `cc-browser skills learn spotify "description"` to persist any new patterns you discover

$ARGUMENTS
