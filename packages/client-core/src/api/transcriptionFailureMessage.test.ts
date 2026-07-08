import { describe, expect, it } from "vitest";

import { transcriptionFailureMessage } from "./client";

// transcriptionFailureMessage turns a raw POST /dictation/{id}/complete failure into a plain-English
// line for the status strip and the error banner. The bug it fixes (issue #1139 follow-up): a server-side
// transcription outage used to reach the user either as a raw server-JSON dump
// ("Transcription returned 502: {...}") or as a misleading blanket "session may be busy" guess. Every
// branch below asserts the user sees a clean, specific, honest sentence - and never the raw server text.

describe("transcriptionFailureMessage", () => {
  it("maps the upstream provider fault (502 upstream_error / 'openai transcription failed') to a clean line, not raw JSON", () => {
    const raw =
      'Transcription returned 502: {"error":{"message":"Transcription failed: openai transcription failed","type":"api_error","code":"upstream_error"}}';
    const msg = transcriptionFailureMessage(raw, 502);
    expect(msg).toBe(
      "The transcription service had a problem and couldn't process your recording. Your recording is saved and will retry.",
    );
    // The raw server response never leaks to the user.
    expect(msg).not.toContain("{");
    expect(msg).not.toContain("upstream_error");
    expect(msg).not.toContain("openai");
  });

  it("maps a circuit-broken upstream (upstream_unavailable, wrapped as 502) to 'temporarily unavailable'", () => {
    const raw =
      'Transcription returned 504: {"error":{"message":"Upstream provider is temporarily unavailable after repeated failures. Retry in 60 seconds.","type":"api_error","code":"upstream_unavailable"}}';
    const msg = transcriptionFailureMessage(raw, 502);
    expect(msg).toContain("temporarily unavailable");
    expect(msg).toContain("saved and will retry");
    expect(msg).not.toContain("{");
  });

  it("maps a timeout (status 504) to a timed-out line", () => {
    expect(transcriptionFailureMessage("upstream_timeout", 504)).toContain("timed out");
  });

  it("maps a gone session (404 / 410) to a session-unavailable line", () => {
    expect(transcriptionFailureMessage("session has exited", 410)).toContain("no longer available");
    expect(transcriptionFailureMessage("session not found", 404)).toContain("no longer available");
  });

  it("maps a missing transcription method to a Settings hint with no false retry promise", () => {
    const msg = transcriptionFailureMessage("no key configured for transcription mode devthrottle", 503);
    expect(msg).toContain("Settings");
    expect(msg).not.toContain("will retry");
  });

  it("maps an undelivered transcript (submit to session failed) to a busy-session line", () => {
    expect(transcriptionFailureMessage("submit to session failed", 502)).toContain(
      "busy or waiting on a prompt",
    );
  });

  it("never surfaces raw server text for an unrecognized error", () => {
    const msg = transcriptionFailureMessage("some totally unexpected internal error blob 0xdeadbeef", 500);
    expect(msg).not.toContain("0xdeadbeef");
    expect(msg).not.toContain("blob");
    expect(msg).toContain("saved and will retry");
  });
});
