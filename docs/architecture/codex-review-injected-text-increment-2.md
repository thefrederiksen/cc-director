# Increment 2 Review: Injected Text Settings And Live Source

## Findings

1. Pi does not follow the "inject nothing and keep launching" behavior.

   The endpoint paths catch `InjectedTextUnavailableException` and return an empty body, so Claude and Codex can start without receiving the declined DevThrottle text. Pi is different. `SessionManager` calls `PiPreambleWriter.WriteForSession` before the backend starts (`src/CcDirector.Core/Sessions/SessionManager.cs:587` to `600`), and `PiPreambleWriter` lets `FleetPreamble.BuildForSession` exceptions propagate (`src/CcDirector.Core/Pi/PiPreambleWriter.cs:41` to `44`).

   Result: if the user chose their own text and `yours.txt` is missing or unreadable, a Pi session likely fails to launch instead of launching with no injected text. That still does not substitute our text, which is the most important consent rule. But it breaks the documented behavior in `docs/public/features/08-injected-text.md:111` to `114`, which says DevThrottle injects nothing and tells the user.

   The caller comment says "the caller decides what a session does with it," but the caller does not decide today. It lets the exception escape.

2. The macOS and Linux Claude hook can print non-hook error bodies to stdout.

   The shell hook prints whatever the hook-output endpoint returns: `curl -s -m 3 "$api/sessions/$sid/fleet-preamble-hook-output" 2>/dev/null || true` (`src/CcDirector.Core/Claude/ClaudeHookInstaller.cs:87`). That is safe for connection failures and for the intended empty body. It is not safe for an HTTP error with a response body, because `curl` without `--fail` still writes that body to stdout.

   One reachable path is hand-editing `yours.txt` into an invalid template after it was saved. `ActiveTemplate` reads it successfully, then `FleetPreambleRenderer.Render` throws `FleetPreambleTemplateException`. `ControlEndpoints` catches only `InjectedTextUnavailableException` around `BuildForSession` (`src/CcDirector.ControlApi/ControlEndpoints.cs:119` to `130`). The endpoint can therefore return a server error body, and the shell hook will print that body as if it were hook output.

   Windows Claude and Codex are safer here because `Invoke-RestMethod` throws on an HTTP error and the script swallows it (`src/CcDirector.Core/Claude/ClaudeHookInstaller.cs:58` to `64`, `src/CcDirector.Core/Codex/CodexHookInstaller.cs:35` to `41`). The shell path should either use `curl -f` or the endpoint should catch all render failures and return an empty text body.

3. Whitespace-only custom text diverges between delivery paths.

   `/sessions/{sid}/fleet-preamble` returns the rendered text as-is (`src/CcDirector.ControlApi/ControlEndpoints.cs:79` to `81`). The hook-output endpoint returns an empty body when the rendered text is whitespace (`src/CcDirector.ControlApi/ControlEndpoints.cs:132` to `133`). Pi writes the rendered text as-is (`src/CcDirector.Core/Pi/PiPreambleWriter.cs:44` to `46`).

   That means a user who saves `"   "` or newlines as their custom text can get different behavior by agent: plain endpoint consumers and Pi receive whitespace, while macOS and Linux Claude receive no hook output. If whitespace means "nothing," every delivery path should normalize that. If whitespace means "my text," the hook-output endpoint should not use `IsNullOrWhiteSpace`.

4. The settings tab does not accurately show the live injected text when the user's file is unreadable.

   On load, if the user selected their own text but `ActiveTemplate` cannot read it, the tab returns `(source, _store.ReadYours() ?? "", ex.Message)` (`src/CcDirector.Avalonia/Controls/InjectedTextView.axaml.cs:81` to `94`). It then applies the normal "YOUR text" banner and sets the editor label to "Your text (this is what your agents get)" (`src/CcDirector.Avalonia/Controls/InjectedTextView.axaml.cs:124` to `134`).

   If the file is missing, the editor is empty because `ReadYours()` returns null, but sessions are not getting that empty editor text as the selected user template. They are getting no preamble through the endpoint paths, and Pi may fail launch. The error explains the read failure, but the label "this is what your agents get" is now false or at least ambiguous.

   The banner should have a third state for "Your text is selected, but unavailable; hook-based agents get no injected text until this is fixed." That would also give the user a clear way to switch back to ours or save a replacement without implying the empty editor is already the live template.

5. The Pi preamble tests are now dependent on real user configuration.

   `PiPreambleWriterTests` pass an output directory, but `PiPreambleWriter` still calls `FleetPreamble.BuildForSession` with the default `InjectedTextStore` (`src/CcDirector.Core/Pi/PiPreambleWriter.cs:44`). The tests assert the DevThrottle default contains identity and command text (`src/CcDirector.Core.Tests/Pi/PiPreambleWriterTests.cs:17` to `23`, `38` to `49`), but that is only true when the real machine config is using ours.

   If the developer running tests has `injected_text.use_yours` set to true, these tests can read that developer's real custom text or fail because the custom file is missing. The store tests know they touch real `config.json` and use a collection guard; the Pi tests do not. This can make the test suite environment-sensitive.

## Answers To The Attack Questions

1. I did not find a path where a user who declined our text is silently given our text. I did find two adjacent problems: Pi can fail before launch instead of injecting nothing, and the tab can label an unavailable custom file as "this is what your agents get."

2. Empty bodies are safe in the PowerShell scripts and the POSIX shell script. Fetch failures are safe when they produce no body. HTTP failures with a body are not safe in the POSIX shell hook because `curl` prints the body to stdout.

3. Making `fleet-preamble-hook-output` async did not obviously break its normal JSON contract. It fixes the signed-in-user omission for macOS and Linux Claude. The remaining contract risk is unhandled render or config exceptions becoming non-hook stdout through the shell script.

4. The tab's normal Save, UseOurs, and WriteMyOwn state transitions look coherent. The stale or wrong state is the unreadable custom file case: the banner says the user's text is live, the editor may show empty text, and the label says the editor content is what agents get.

5. The docs are false for Pi in the missing custom text case if the exception still aborts launch. The docs say DevThrottle injects nothing and tells the user. The code does that for endpoint-based hooks, not for Pi.

## Verification

I ran `dotnet test --filter "InjectedText|FleetPreamble|PiPreamble"`. It passed in this environment: 74 core tests, 2 gateway tests, and 4 Avalonia tests matched and passed. The Pi test risk above can still be real because this machine's current config did not exercise `use_yours=true` with a custom template.
