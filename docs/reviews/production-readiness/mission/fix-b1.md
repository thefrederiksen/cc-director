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
it is a single safe path segment AND its resolved path stays inside the tenant's voice-audio dir:

- single ordinary file-name component: equals its own `Path.GetFileName`, not `.` / `..`, no
  `/ \ :`, no `Path.GetInvalidFileNameChars`; and
- canonical containment: `Path.GetFullPath(combined)` must start with the tenant's voice-audio root.

A legitimate GUID session id has none of these traits and passes untouched. This mirrors, one level
down, the exact rule the tenant partition already applies to the directory name.

## Revert-proof

Each guard is independently pinned (single-primitive reverts, full-assembly build confirmed 0/0):

- Remove the DELETE guard only -> `..._cannot_delete_another_tenants_clip` reddens; the other four
  stay green.
- Remove the SAVE guard only -> the write and overwrite tests redden (and the delete test, whose
  victim is overwritten before delete).

## Test evidence (after fix)

- `WingmanVoiceSidPathTraversalTests`: 5/5 pass (3 attack + 2 controls: separator id writes nothing,
  normal GUID still persists).
- All voice + wingman Gateway tests: 366/366 pass.
- `WingmanVoiceTenantPartitionTests` (solo): 20/20 pass.
- `VoiceUploadStoreTenantPartitionTests` (solo): 15/15 pass.
- Gateway + Gateway.Tests build: 0 warnings / 0 errors.

## Files

- `src/CcDirector.Gateway/Wingman/WingmanVoiceService.cs` - `SafeClipPath` + guarded save/delete.
- `src/CcDirector.Gateway.Tests/WingmanVoiceSidPathTraversalTests.cs` - reproduction + revert-proof.
