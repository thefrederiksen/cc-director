---
name: cc-settings-api
description: RETIRED - the Director's Control API no longer exists (the remove-the-network-port mission deleted the listener), so settings can no longer be driven over local HTTP. Kept only to say where settings editing lives now.
---

# CC Director Settings API Skill - retired

This skill drove a running Director's `GET /settings` and `PUT /settings` loopback routes. The
remove-the-network-port mission (phase 5, 2026-08-03) deleted the Director's HTTP listener
entirely, so those routes - and the loopback port they lived on - no longer exist on any current
build. The `configure_settings.py` script in this directory cannot work against a current
Director and must not be reached for.

Where settings editing lives now:

- **A person**: the desktop application's Settings dialog (in process, no network). Gateway
  changes made there re-apply live, with no restart.
- **An agent on the same machine**: `cc-devthrottle settings get <key>` /
  `cc-devthrottle settings set <key> <value>`, which read and write this machine's `config.json`
  directly on disk. Note the difference from the old API: a file edit does not tell the running
  Director to re-apply its Gateway connection - a gateway change made this way lands at the next
  Director start, so prefer the Settings dialog for connection changes on a running Director.
- **Remote configuration** rides the Gateway's tunnel verbs (the same cores the old routes ran),
  driven from the Cockpit.

If a task genuinely needs a settings capability the Gateway path does not offer, that is a gap to
report on the thefrederiksen/devthrottle repository, not a reason to resurrect a local port.
