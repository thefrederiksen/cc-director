# Gateway data boundaries

Status: implemented in internal issue #494 on 2026-07-22.

This document replaces the former telemetry-centralization plan. DevThrottle does not collect or
forward first-party behavioral, startup, login, or usage telemetry. There is therefore no telemetry
consent setting in the Director, Gateway, installer, Cockpit, or hosted service.

## Network boundary

The Gateway remains the network front door for product features. A Director, Cockpit, or mobile
client can send normal feature requests to its configured Gateway. The Gateway may contact an
external service only when that is necessary to complete the user's requested feature, including:

- account sign-in, status, device management, credits, nickname, notifications, and account email;
- hosted AI, speech-to-text, text-to-speech, and model operations;
- user-configured provider APIs and other explicitly invoked integrations.

Those requests are service traffic, not telemetry. They carry the data required to complete the
requested operation and remain subject to the relevant service and account boundaries.

The following data flows do not exist:

- Director to a DevThrottle telemetry API;
- Director to Gateway to a DevThrottle telemetry API;
- Gateway startup, login, usage, or behavioral event forwarding;
- retry queues or environment-variable endpoint overrides for event forwarding;
- hosted-Gateway telemetry consent reads or writes.

Self-hosting does not enable a separate reporting path. A self-hosted Gateway never sends startup,
login, usage, or behavioral events to DevThrottle.

## Local history and diagnostics

Two owner-facing features retain bounded local data. They are deliberately named for their product
purpose and are not sent to DevThrottle.

### Transcription History

- Self-hosted only; the shared hosted Gateway refuses these routes.
- Stores timing, outcome, word counts, correction terms, and a turn identifier.
- Does not store raw transcript text, cleaned transcript text, or provider error bodies.
- Retained for 30 days.
- Associated troubleshooting audio stays local for at most 24 hours and is capped at 500 clips.
- Read through `GET /transcription/stats`, `GET /transcription/turns`, and
  `GET /transcription/terms`.
- History and associated troubleshooting audio are cleared together through
  `DELETE /transcription/history` or the Cockpit control.

### Car Mode Diagnostics

- Stored locally and partitioned by a server-derived device hash.
- Stores per-turn timing and playback diagnostic fields, never prompts or replies.
- Retained for 90 days with a growth cap.
- Written through `POST /carmode/diagnostics` and read through
  `GET /carmode/diagnostics/data` or the local diagnostics page.
- Cleared per device through `DELETE /carmode/diagnostics` or the page control.

Ordinary application logs and troubleshooting records are operational diagnostics. They must not
be repurposed as behavioral analytics or forwarded as first-party usage events.

## Removed surfaces

The removal covers all first-party event collection and consent surfaces:

- startup and login event producers;
- usage and authentication event files;
- Gateway relay, startup-event, and consent endpoints;
- event forwarding clients, token sources, queues, retry workers, and URL overrides;
- telemetry settings, configuration snapshots, caches, installer choices, and Cockpit navigation;
- generated client contracts for the retired routes;
- tests and documentation that described those surfaces as current behavior.

Startup performs a one-way, idempotent cleanup. It removes retired consent keys, queued event
files, authentication/usage event files, the old Car Mode event file, and the old transcription-log
directory. Queued payloads are deleted without being read or forwarded. Unrelated configuration,
Gateway settings, and account credentials are preserved.

## Backend follow-through

The desktop repository no longer calls the retired backend routes or sends the `telemetry_enabled`
field. Removing or disabling the corresponding backend handlers and stored fields is tracked as the
server-side portion of internal issue #494. That deployment is separate from this repository change.

## Review rule

A future feature that sends product behavior, startup state, login events, or usage measurements
off the user's machine requires a new explicit product and privacy decision. It must not be added by
reusing local history, diagnostics, logs, or account credentials.
