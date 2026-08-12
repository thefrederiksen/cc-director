# Contributing to DevThrottle

Thanks for being here. DevThrottle is a real product with a company behind it, and it is also genuinely open source under the MIT license -- the code you see is the code we ship, including the gateway we host. Outside contributions are welcome.

This page tells you how to build it, what we are looking for, and what will get a change sent back.

## Before you write code

**Small fixes -- just send them.** A typo, a broken link, a clear bug with an obvious fix, a missing test: open a pull request directly. No permission needed.

**Anything bigger -- talk to us first.** Open an [issue](https://github.com/thefrederiksen/devthrottle/issues) or start a [discussion](https://github.com/thefrederiksen/devthrottle/discussions) describing what you want to change and why. This is not bureaucracy, it is us saving you a wasted weekend: a lot of what looks like a gap is either already being built or was deliberately left out. Wait for a reply before you start.

**Things we are especially glad to receive:** support for another command-line coding agent, bug fixes with a test that fails before and passes after, documentation that was wrong or missing, and accessibility fixes.

**Things we will probably decline:** large refactors of code that works, swapping out a dependency or framework, new features that were not discussed first, and changes to pricing, licensing, or anything that talks to the hosted service's billing.

## Building it

You need the **.NET 10 SDK** (`global.json` pins 10.0.301) and, for the browser front ends, **Node.js 20 or later**. Nothing else -- no administrator rights, no Docker, no database.

```powershell
git clone https://github.com/thefrederiksen/devthrottle
cd devthrottle
dotnet build cc-director.sln
```

The desktop application is Windows and macOS (Apple Silicon). The gateway, the cockpit, and the mobile application build anywhere.

## Running the tests

One command, and it is the gate:

```powershell
.\scripts\test-local.ps1
```

That runs about 3,500 tests in roughly two minutes. **Your pull request needs it green.** Two heavier suites are held back from the default run and are worth running if you touched the gateway:

```powershell
.\scripts\test-local.ps1 -Parked
```

For the browser front ends:

```bash
npm install
npm run typecheck
npm run lint
```

## Where things live

| Path | What it is |
|---|---|
| `src/CcDirector.Avalonia` | The desktop application -- the window you actually look at |
| `src/CcDirector.Core` | The engine: sessions, agents, state, voice, configuration |
| `src/CcDirector.Gateway` | The gateway -- the same code whether we host it or you do |
| `src/CcDirector.Terminal*` | The terminal emulator and its renderer |
| `apps/cockpit`, `apps/mobile` | The web cockpit and the mobile application (React) |
| `packages/client-core` | Shared front-end code. **Settings live here, not in either shell** |
| `tools/` | The `cc-*` command line tools and the installer |
| `docs/public` | The documentation users read |
| `scripts/` | Build, test, and release scripts |

## House rules

These are not style preferences, they are the things that will get a change sent back. The full versions are in [docs/CodingStyle.md](docs/CodingStyle.md) and [docs/VisualStyle.md](docs/VisualStyle.md); these are the five that catch people out.

**No fallback programming.** If something can fail, fix the cause or fail loudly with a message that says what to do about it. Do not catch an error and quietly carry on with a degraded result -- that hides the real problem and makes it somebody else's bug later.

```csharp
// No
try { return GetValue(); } catch { return "Unknown"; }

// Yes
var value = GetValue();
if (value is null)
    throw new InvalidOperationException("Value not available");
return value;
```

**Log entry, exit, and errors** in public methods, in the house format: `FileLog.Write($"[ClassName] MethodName: context={value}")`.

**The user interface never blocks.** Every action gives visible feedback within a tenth of a second. Show the window first and load into it; never do file or network work on the user interface thread.

**Plain ASCII in all program output.** No emoji, no arrows, no check marks -- not in console output, log files, error messages, or code comments. Write `[OK]`, `ERROR`, `->`, `WARNING`. Windows terminals and log files mangle the rest, and it has crashed things before.

**Tests come with the change.** A bug fix brings a test that fails before your fix and passes after. Name them `MethodName_Scenario_ExpectedResult`.

## Pull requests

- **One thing per pull request.** A change that fixes a bug and also renames forty files is very hard to review and will be slow.
- **Say what you did and how you know it works.** A screenshot for anything visual, and the test you added.
- **Write commit messages as yourself.** Please do not add "generated with" footers or co-authored-by trailers naming a tool or an assistant -- whichever editor or assistant you used, the commit is yours.
- **Keep it in plain English.** Spell words out in commits, comments, and documentation. We avoid abbreviations and acronyms on purpose.
- Expect a first reply within a few days.

## Licensing

DevThrottle is [MIT licensed](LICENSE). By opening a pull request you agree that your contribution is licensed under the MIT license too, and that it is your own work or that you have the right to submit it. There is no separate agreement to sign.

## Questions

Anything that is not a bug report goes in [Discussions](https://github.com/thefrederiksen/devthrottle/discussions) -- how something works, whether an idea is welcome, or how to get set up. Security problems are different: see [SECURITY.md](SECURITY.md), and please do not open a public issue for one.
