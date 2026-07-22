# B1 - Voice-audio session-id path traversal (BLOCKER) - FIXED

## Verdict

REAL gap, reproduced on main, fixed, revert-proofed.

## The gap

`WingmanVoiceService` partitions voice state per tenant and validates the TENANT id hard
(`CanonicalTenantKey` / `PartitionDirectoryFor` refuse traversal, case-variants, and non-minted
shapes, with a canonical-containment belt-and-braces). But the SESSION id, which becomes the clip
FILE NAME under `<tenant>/voice-audio/`, was concatenated raw with no validation:

- `SaveReadyAudio`: `File.WriteAllBytes(Path.Combine(audioDir, sid + ".mp3"), ...)` and the `.json`.
- `DeleteReadyAudio`: `Path.Combine(audioDir, sid + ".json")` / `+ ".mp3"`, then `File.Delete`.

The session id is caller-controlled on the PERSISTING path. A hostile Director advertises any
non-empty string as a pushed session id; the `/sessions/voice-mode/all` fan-out
(`GatewayWingmanVoiceEndpoint.cs`, only a `!string.IsNullOrEmpty(s.SessionId)` gate) and the
turn-end narration sweep carry that id into `GenerateAsync -> StoreSpokenAsync -> StoreReady ->
SaveReadyAudio` with no `Guid.TryParse` gate (the interactive request endpoints that DO gate on
`Guid.TryParse` are a different door and do not cover this path).

A session id of the shape `"../../<other-tenant>/voice-audio/<victim>"` therefore walks the write
out of the caller's own partition and into another tenant's directory - overwriting that tenant's
clip on the save path, or deleting it on the delete path. Cross-tenant disclosure/destruction of
customer content on the hosted Gateway.

## Reproduction (on main, before the fix)

`WingmanVoiceSidPathTraversalTests` - three attack tests failed on unpatched main:

- `A_traversal_session_id_cannot_write_into_another_tenants_voice_audio_directory` - a planted
  file appeared in tenant B's `voice-audio` directory.
- `A_traversal_session_id_cannot_overwrite_another_tenants_clip` - tenant B's clip bytes were
  overwritten with the attacker's.
- `A_traversal_session_id_cannot_delete_another_tenants_clip` - tenant B's clip was deleted.

## The fix

New private `SafeClipPath(tenant, sid, extension)` in `WingmanVoiceService`, applied on BOTH the
save and the delete sink. It refuses the id (returns null -> the sink writes/deletes NOTHING) unless
it passes a strict allow-list AND its resolved path stays inside the tenant's voice-audio dir:

- strict safe-id allow-list: non-empty, not `.` / `..`, no longer than `MaxSidLength` (128), and
  every character drawn from `[A-Za-z0-9._-]`; and
- canonical containment: `Path.GetFullPath(combined)` must start with the tenant's voice-audio root.

A legitimate GUID session id is entirely allow-list characters, well under the length bound, and
passes untouched. This mirrors, one level down, the exact rule the tenant partition already applies
to the directory name.

### Harden after Codex review (audit B1, PR #1990)

Codex's review of the first cut found the guard was a separator/invalid-char DENYLIST that equals
its own `Path.GetFileName` and bans `/ \ :` plus the platform's invalid chars. That still ACCEPTED
two shapes:

- a PERCENT-ENCODED traversal such as `%2e%2e%2f%2e%2e%2fescape` - no literal separator, no invalid
  char, so it slipped the denylist and built a bizarrely-named clip file; and
- an UNBOUNDED, over-long segment (a 300-char id) - no length check at all.

The denylist was replaced with a strict ALLOW-LIST (`[A-Za-z0-9._-]` only, so `%` is rejected) plus
a `MaxSidLength = 128` bound, applied BEFORE any path is built. This is defense in depth on TOP of
the canonical containment, which is unchanged. An allow-list cannot be out-guessed the way a
denylist can.

## Revert-proof

Each guard is independently pinned (single-primitive reverts, full-assembly build confirmed 0/0):

- Remove the DELETE guard only -> `..._cannot_delete_another_tenants_clip` reddens; the other four
  stay green.
- Remove the SAVE guard only -> the write and overwrite tests redden (and the delete test, whose
  victim is overwritten before delete).

## Test evidence (after fix + harden)

- `WingmanVoiceSidPathTraversalTests`: 7/7 pass - 3 traversal attacks, 1 separator id, 1 normal-GUID
  control, plus the two Codex-review additions: a percent-encoded traversal id
  (`%2e%2e%2f%2e%2e%2fescape`) writes nothing, and a 300-char id is refused before any directory is
  created. Both new tests are revert-proofed: restore the old separator/invalid-char denylist and
  both redden (the percent id writes its clip file; the over-long id's refused save no longer skips
  `Directory.CreateDirectory`, so the voice-audio directory appears), while the original five stay
  green.
- All voice + wingman Gateway tests: 368/368 pass.
- `WingmanVoiceTenantPartitionTests` (solo): 20/20 pass.
- `VoiceUploadStoreTenantPartitionTests` (solo): 15/15 pass.
- Gateway + Gateway.Tests build: 0 warnings / 0 errors.

## Files

- `src/CcDirector.Gateway/Wingman/WingmanVoiceService.cs` - `SafeClipPath` + guarded save/delete.
- `src/CcDirector.Gateway.Tests/WingmanVoiceSidPathTraversalTests.cs` - reproduction + revert-proof.
