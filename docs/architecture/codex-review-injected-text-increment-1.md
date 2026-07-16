# Increment 1 Review: Injected Text Refactor

## Findings

1. The renderer still substitutes known tokens inside larger bracketed prose.

   `FleetPreambleRenderer.Substitute` starts at any `[` and scans to the next `]`. If the first bracketed run is not a known token, it appends one character and continues scanning inside that same bracketed text. That means `[[SESSION_ID]]` renders as `[a3dfb85e-49dd-442a-9e36-40fc44838783]`, and `see [[MACHINE]]` renders as `see [MACHINE_A]`.

   This does not differ from the old builder for the shipped default, because the default does not contain that shape. It does matter for the renderer contract. The comments say unknown bracketed prose survives verbatim, and the test suite proves `[[SESSION_UNKNOWN]]` survives, but it does not prove `[[SESSION_ID]]` survives. A user could reasonably write double-bracketed notes, examples, or literal placeholder documentation and silently get a substituted value inside them.

   If the intended rule is "any exact known token anywhere is replaced," the comments and tests should say that. If the intended rule is "only a whole bracketed token is replaced," the scanner needs a boundary rule so a token preceded by another `[` or followed by another `]` is treated as ordinary text.

2. Deleting the migration proof would currently remove the only byte-for-byte default guard.

   `FleetPreambleTemplateMigrationTests` says it is temporary and that `FleetPreambleGoldenTests` carries the lasting guard, but no such test exists in this worktree. The remaining `FleetPreambleTests` assert important substrings and policy lines, not byte identity for the shipped default.

   The legacy-copy test should not live forever as the rule for future wording. That part of the argument to delete is right: once the default text intentionally changes, a frozen old builder becomes the wrong oracle. But deleting it immediately after this commit is only safe if a current-default golden guard replaces it. Otherwise, a later edit can silently change spacing, blank lines, command indentation, or the no-signed-in-user gap without a byte-level failure.

   My recommendation is: keep the migration test in the pure-refactor commit so the commit proves zero behavior change. After that, delete `LegacyFleetPreamble` only in the same change that adds a lasting golden test against an approved text fixture or snapshot for `FleetPreambleTemplate.Default`. That snapshot can be updated intentionally when the shipped default changes. It should not compare against old code forever.

3. Unknown placeholders should not be rejected by the renderer, but they must be surfaced before user text becomes live.

   Leaving unknown bracketed text verbatim is the right renderer behavior because the shipped default begins with `[CC Director fleet]`, and users will write ordinary bracketed prose. A hard reject in `Render` would make harmless text fail at session launch and would turn a simple text template into a fragile language.

   The silent failure remains real for likely mistakes such as `[SESSION-ID]`, `[SESSIONID]`, or `[USER_MAIL]`. If those are left verbatim, the agent receives plausible-looking but wrong instructions. The right split is: the renderer leaves unknown bracketed text alone, and the future settings save or preview path reports suspicious unknown all-capital bracket tokens as warnings. That preserves bracketed prose while catching placeholder typos before they reach agents.

4. User identity placeholders outside the signed-in block can still create broken prose.

   `Render` maps `[USER_NAME]` and `[USER_EMAIL]` to empty strings when no user is signed in. That is safe for the current default because those tokens are inside `[IF_SIGNED_IN]`. It is not safe for future custom templates. A user can write `The user is [USER_NAME] ([USER_EMAIL]).` outside the block and get `The user is  ().` with no validation error.

   This is not a behavior change for increment 1, but the renderer now defines the future template contract. The save or preview validation should warn when user identity tokens appear outside `[IF_SIGNED_IN]`, or the renderer should leave those tokens verbatim outside a signed-in block. Empty strings are the easiest behavior to miss.

## Answers To The Three Questions

1. The single left-to-right pass is correct for preventing substituted values from being substituted again. It preserves old-builder behavior for values such as a session named `[MACHINE]`. The remaining corruption is not re-substitution; it is the scanner matching known tokens inside larger bracketed prose such as `[[SESSION_ID]]`.

2. Delete the legacy migration test only after the pure-refactor commit has used it to prove byte identity, and only if a lasting golden guard replaces it. Do not leave a frozen old builder as the permanent oracle for wording updates, but do not remove the only byte-for-byte protection and rely only on substring tests.

3. Leaving unknown placeholders verbatim wins in the renderer. Rejecting them there breaks ordinary bracketed prose and the shipped `[CC Director fleet]` prefix. The missing piece is warning or validation in the user-facing edit path for suspicious placeholder-shaped typos, especially all-capital bracket tokens and identity tokens outside the signed-in block.
