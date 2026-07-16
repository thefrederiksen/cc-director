# First Impressions Review: Injected Text

This review looks only for ways the refactor from hard-coded string building to editable templates could silently change, drop, or corrupt the text that reaches agents.

## Findings

1. Placeholder mistakes can degrade into plausible but wrong text.

   The current code derives `shortId`, substitutes the display name, omits the user identity line when there is no signed-in email, and then joins a fixed list of lines. A template renderer can silently leave `[SESSION_ID]`, `[SHORT_SESSION_ID]`, `[USER_EMAIL]`, or similar placeholders in the delivered text if the placeholder names drift between the default template, the user interface, and the renderer. That failure would still produce readable context, so agents may not report it. The first line and the identity line need tests that prove every supported placeholder is replaced and that unknown placeholders are either rejected or surfaced clearly.

2. Optional user identity is easy to render as broken prose.

   `FleetPreamble.Build` currently omits the signed-in user sentence entirely when there is no email. In a template, a user may leave a sentence that depends on user variables while the renderer has no user value. If missing variables become empty strings, agents could receive text such as `The user of this session is ().` or a sentence that binds "me" to nobody. That is worse than omission because it looks intentional. The design needs an explicit conditional block or a rule that blocks saving or using a template that references unavailable identity variables.

3. Newline behavior can change the command block without failing any launch path.

   The current implementation uses `string.Join("\n", lines)` and preserves indentation for the command list and continuation lines. Loading a template from a file introduces line ending conversion, editor trimming, final newline differences, and possible indentation normalization. Any of those can change the displayed command list while still producing valid context. Tests should compare exact text, including blank lines and leading spaces, for the shipped default template on Windows.

4. Escaping and quoting rules can damage command examples.

   The existing text contains quoted names, quoted command arguments, apostrophes, angle brackets, parentheses, and a literal `--everyone` flag. Moving this into a stored template creates several places where escaping can be accidentally interpreted: project file embedding, resource copy, settings storage, serialization, markup preview, and text area round trips. A silent escape bug could turn `cc-devthrottle session rename "name"` into malformed advice or make angle-bracket placeholders look like variables. The default template should be tested after every storage and delivery step, not only after the renderer runs.

5. The short session identifier is computed, not a simple field.

   The current code shortens the session identifier to eight characters only when possible. If the template system exposes both full and short identifiers, the short form must preserve that rule. A naive renderer could use a fixed substring and throw for short identifiers, or it could expose only the full identifier and force the default text to duplicate it. Either outcome can break tests loudly, but a subtler failure is using the full identifier in both places and making session names harder to scan without any error.

6. User custom text can remove operational instructions silently.

   The mission seed says deleting fleet commands is the user's right. That also means the main product can stop giving agents the instructions required to contact peers while still showing "custom text is active" as if all is well. If the user interface only labels the source and does not show which known variables or command sections are missing, an operator may not realize they have removed fleet awareness until agents fail to coordinate. At minimum, the preview should make the exact rendered text visible for a real or sample session.

7. Delivery paths may diverge after the template is introduced.

   The seed names several delivery mechanisms: native hooks, event bus, extension, instruction file, and an endpoint used by non-Claude agents. If each path loads or renders the template independently, small differences in caching, file paths, line endings, and fallback behavior can make agents receive different preambles. The current single `FleetPreamble.Build` method is a strong consistency point. The refactor should keep one render function used by all delivery paths, with tests for the endpoint and at least one file or hook writer proving they receive the same rendered string.

8. Fallback behavior can hide a broken custom template.

   The requirements say custom text wins over the shipped default and that there is no merging. If a custom template file is unreadable, invalid, or references unsupported variables, silently falling back to the shipped default violates the user's choice and makes the settings tab untrustworthy. If the application must fall back to keep agents usable, the settings tab and logs need to show that the live text is no longer the user's custom text. Otherwise, the system should fail clearly before launching with different text than the user selected.

9. Updates to the shipped default can be present but not actually used in preview or launch.

   The owner asked that our updates are always downloaded and always visible, even when custom text is active. That creates two live versions of the same concept. A common silent failure would be showing the current shipped default in the settings tab while launch code still reads an older embedded resource or a cached copy. The shipped default should have a single source of truth, and tests should prove the settings preview and launch rendering read that same source.

10. Template editing can make policy text appear official when it is user-authored.

   The seed requires the user interface to show when the live text is theirs. The same distinction matters in what reaches agents. If a custom template keeps the `[CC Director fleet]` prefix or other official-looking language, agents cannot infer that the instructions are user-authored. That may be acceptable, but it is a silent trust change: agents could treat a local custom policy as a product policy. The product should decide whether rendered custom text includes any source marker or whether the distinction exists only in the settings interface.

11. Agent-specific constraints can corrupt a single shared template.

   The seed says one text currently reaches several agents through different mechanisms. Those mechanisms may have different limits or formatting rules. A template that renders correctly for one agent may be truncated, escaped, wrapped, or stored differently for another. Since the desired model appears to be all agents at once, the tests should include the most restrictive delivery path and verify that the full rendered text arrives intact.

12. Documentation can lag behind the actual injected text.

   The mission exists because users cannot see what is injected. If the shipped default lives in one place and documentation quotes or describes it somewhere else, the documentation can become stale immediately. That would silently recreate the same consent problem in a softer form. Documentation should point users to the settings tab and describe the mechanism, not duplicate the preamble text unless there is an automated check tying them together.

## Suggested Test Shape

The highest value test is a golden test that renders the shipped default template with a full sample session, a named session, a machine, a repository path, and a signed-in user, then compares it byte for byte to the current `FleetPreamble.Build` output. A second test should render without a user and prove the identity sentence is absent, not empty. A third test should exercise the same renderer through the non-Claude endpoint and one file or hook delivery path so the launch routes cannot drift.
