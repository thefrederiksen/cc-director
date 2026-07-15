using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// An architecture fitness function for the ONE rule the mobile app keeps breaking: a session screen
/// must FIT THE VISIBLE SCREEN, and the PAGE must never scroll.
///
/// Why this test exists, in the owner's words: "this is the tenth time or something that this bug has
/// been reintroduced into our mobile application. We need to figure out a more consistent way of
/// never having this issue."
///
/// The history, every entry the same bug wearing a different hat:
///   #1058  session screens: swap vh -> dvh
///   #1349  voice speaking state: fit the viewport
///   #1351  Terminal + Chat: overflow-clip shell, no page scroll
///   #1404  Car Mode v4: the primary button renders below the toolbar, cut off
///   #1408  Car Mode v5: dvh ABANDONED - pin to window.visualViewport.height instead
///
/// The root cause of the RECURRENCE (not of any single instance): on the owner's Android PWA, CSS
/// `100dvh` does not track the visible height - the box stays taller than the screen, the page gains
/// a sliver of scroll, and one swipe slides the top row away. It cannot be caught by eye in an
/// emulator, which reports zero page scroll and renders it perfectly. Every fix that trusted a CSS
/// viewport unit came back. Only #1408's approach - taking the height from
/// window.visualViewport.height - has held on the real device.
///
/// So the contract is: BOTH full-screen shells take their height from --app-vh (published by the one
/// shared useVisibleViewportHeight hook, mounted once in the app shell), never from a bare viewport
/// unit; and neither over-constrains itself with `inset: 0`, which silently makes the height
/// declaration a no-op (that is precisely what Car Mode v4 shipped).
///
/// This is a C# test on purpose. CI runs `dotnet test` and NOTHING ELSE - there is no JavaScript test
/// step in .github/workflows/ci.yml - so a vitest guard would never run, and a guard nobody runs is
/// not a guard. It reads the mobile source as text, the same way NoCrossMachineLoopbackGuardTests
/// pins the loopback policy.
///
/// Scope, stated rather than hidden: this pins the MECHANISM (the shells size from the shared
/// variable, the hook is mounted, nobody reintroduces a private one). It cannot prove pixels on a
/// real phone. It exists to stop the specific regression that has actually happened five times -
/// someone "simplifying" a shell back to a plain dvh box.
/// </summary>
public sealed class MobileViewportContractTests
{
    private const string StylesPath = "apps/mobile/src/styles.css";
    private const string ShellPath = "apps/mobile/src/main.tsx";
    private const string HookPath = "apps/mobile/src/hooks/useVisibleViewportHeight.ts";

    /// <summary>The full-screen shells. Every screen that fills the window must use one of these.</summary>
    private static readonly string[] FullScreenShells = [".terminal-screen", ".car-screen"];

    private const string Why =
        "\n\nWHY: on the owner's Android PWA, CSS 100dvh does not track the visible height - the box stays "
        + "TALLER than the screen, the page gains a sliver of scroll, and one swipe hides the top row. "
        + "It looks perfect in an emulator. This has been re-fixed at least five times (#1058, #1349, "
        + "#1351, #1404, #1408); only pinning to window.visualViewport.height (#1408) has ever held. "
        + "Take the height from --app-vh (see apps/mobile/src/hooks/useVisibleViewportHeight.ts), not "
        + "from a CSS viewport unit.";

    [Fact]
    public void FullScreenShells_TakeTheirHeightFromTheSharedVisibleViewportVariable()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(), StylesPath));
        var offenders = new List<string>();

        foreach (var shell in FullScreenShells)
        {
            var block = RuleBlock(css, shell);
            Assert.False(block is null, $"{StylesPath} no longer defines a `{shell}` rule. If a shell was renamed, update this test - do not delete the contract.{Why}");

            // The LAST height declaration wins in CSS. That one must be the pinned variable; earlier
            // vh/dvh lines are legitimate fallbacks for the first paint.
            var heights = Regex.Matches(block!, @"(?<!max-|min-)\bheight\s*:\s*([^;]+);")
                               .Select(m => m.Groups[1].Value.Trim())
                               .ToList();
            if (heights.Count == 0)
                offenders.Add($"{shell}: declares no height at all.");
            else if (!heights[^1].Contains("var(--app-vh", StringComparison.Ordinal))
                offenders.Add($"{shell}: its EFFECTIVE height is `{heights[^1]}`, not var(--app-vh, ...). A CSS viewport unit is exactly what keeps regressing.");

            var maxHeights = Regex.Matches(block!, @"\bmax-height\s*:\s*([^;]+);")
                                  .Select(m => m.Groups[1].Value.Trim())
                                  .ToList();
            if (maxHeights.Count > 0 && !maxHeights[^1].Contains("var(--app-vh", StringComparison.Ordinal))
                offenders.Add($"{shell}: its EFFECTIVE max-height is `{maxHeights[^1]}`, not var(--app-vh, ...).");
        }

        Assert.True(offenders.Count == 0, "A full-screen mobile shell is not pinned to the visible viewport:\n  " + string.Join("\n  ", offenders) + Why);
    }

    [Fact]
    public void FullScreenShells_DoNotOverConstrainThemselvesWithInsetZero()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(), StylesPath));
        var offenders = new List<string>();

        foreach (var shell in FullScreenShells)
        {
            var block = RuleBlock(css, shell);
            if (block is null) continue;

            // `inset: 0` (or an explicit bottom) sets top AND bottom, so the height is derived from the
            // tall toolbar-hidden layout viewport and the height declaration is IGNORED. Car Mode v4
            // shipped this and the primary button rendered off-screen (#1404 -> #1408).
            if (Regex.IsMatch(block, @"\binset\s*:\s*0"))
                offenders.Add($"{shell}: uses `inset: 0`, which over-constrains the box so its height declaration is ignored. Anchor top/left/right only.");
            if (Regex.IsMatch(block, @"\bposition\s*:\s*fixed") && Regex.IsMatch(block, @"(?<!-)\bbottom\s*:\s*0"))
                offenders.Add($"{shell}: is fixed AND anchors `bottom: 0`, which over-constrains its height the same way `inset: 0` does.");
        }

        Assert.True(offenders.Count == 0, "A full-screen mobile shell over-constrains its own height:\n  " + string.Join("\n  ", offenders) + Why);
    }

    [Fact]
    public void TheVisibleViewportHook_ExistsAndIsMountedOnceInTheAppShell()
    {
        var root = GetRepoRoot();

        var hookFull = Path.Combine(root, HookPath);
        Assert.True(File.Exists(hookFull), $"{HookPath} is missing. It is the ONE place the app learns how tall the screen actually is.{Why}");

        var hook = File.ReadAllText(hookFull);
        Assert.True(hook.Contains("visualViewport", StringComparison.Ordinal),
            $"{HookPath} no longer reads window.visualViewport. That reading is the whole point - it is the only source that has held on the real device.{Why}");
        Assert.True(hook.Contains("--app-vh", StringComparison.Ordinal),
            $"{HookPath} no longer publishes --app-vh, which the stylesheet sizes the shells from.{Why}");

        // A plain Contains() here was a FAKE guard: commenting the call out (`// useVisibleViewportHeight();`)
        // still contains the string, so the test passed while the hook was not mounted at all - the exact
        // regression it is supposed to catch. Only a real, uncommented CALL counts.
        var shellLines = File.ReadAllLines(Path.Combine(root, ShellPath));
        var mounted = shellLines.Any(line =>
        {
            var code = line.Trim();
            if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("*", StringComparison.Ordinal)) return false;
            return Regex.IsMatch(code, @"(^|[^.\w])useVisibleViewportHeight\s*\(\s*\)\s*;");
        });
        Assert.True(mounted,
            $"{ShellPath} does not CALL useVisibleViewportHeight(). Mounted once in the app shell, it fits EVERY screen; if it is not mounted, --app-vh is never set and every shell silently falls back to the dvh that does not work on the device.{Why}");
    }

    [Fact]
    public void NoScreen_ReintroducesItsOwnPrivateViewportHeightVariable()
    {
        var root = GetRepoRoot();
        var mobileSrc = Path.Combine(root, "apps", "mobile", "src");
        var offenders = new List<string>();

        // One mechanism. Car Mode used to own a private --car-vh and Chat/Terminal/Voice trusted dvh;
        // that split is exactly how a fix lands on one screen and the bug survives on the others.
        var privateVar = new Regex(@"--(?!app-vh)[a-z0-9-]*-vh\b");

        foreach (var file in Directory.EnumerateFiles(mobileSrc, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ext is not (".ts" or ".tsx" or ".css")) continue;

            foreach (var (line, i) in File.ReadAllLines(file).Select((l, i) => (l, i)))
            {
                // A prose mention of the retired name in a comment is fine; a real declaration is not.
                if (!privateVar.IsMatch(line)) continue;
                var isDeclarationOrUse = line.Contains("setProperty", StringComparison.Ordinal)
                                         || Regex.IsMatch(line, @"var\(--(?!app-vh)[a-z0-9-]*-vh")
                                         || Regex.IsMatch(line, @"^\s*--(?!app-vh)[a-z0-9-]*-vh\s*:");
                if (isDeclarationOrUse)
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A screen has reintroduced its own private viewport-height variable. There is ONE: --app-vh, from the shared hook. "
            + "A per-screen copy is how this bug survives on the screens that did not get the fix:\n  "
            + string.Join("\n  ", offenders) + Why);
    }

    /// <summary>Returns the body of the FIRST rule whose selector list contains <paramref name="selector"/> exactly.</summary>
    private static string? RuleBlock(string css, string selector)
    {
        // Comments must go first: this file documents the rules heavily, and a comment sitting above a
        // rule would otherwise be swallowed into that rule's selector text (and its commas would split it).
        var stripped = StripComments(css);
        foreach (Match m in Regex.Matches(stripped, @"([^{}]+)\{([^{}]*)\}"))
        {
            var selectors = m.Groups[1].Value.Split(',').Select(s => s.Trim());
            if (selectors.Any(s => s == selector)) return m.Groups[2].Value;
        }
        return null;
    }

    private static string StripComments(string css) => Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
