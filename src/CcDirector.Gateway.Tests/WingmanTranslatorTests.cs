using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Wingman;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #531: the wingman as the translator of a working session. These tests exercise
/// the translation logic with a fake <see cref="IAgentBrain"/> - no live model, no audio -
/// which is exactly the testable foundation the Wingman Text tab is built on. They prove
/// the mechanical guarantees (faithful carry-through, context cleared every turn, fail-loud
/// on a broken contract, speech cleanup, and that the only dependency is a real-session
/// brain - never a <c>--print</c> process). Human judgement of summary quality comes from
/// the HTML QA report this file emits when <c>CC531_PROOF_DIR</c> names an output directory;
/// a normal test run writes no files.
/// </summary>
public sealed class WingmanTranslatorTests
{
    private readonly ITestOutputHelper _out;

    public WingmanTranslatorTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// A fake warm brain: wraps whatever spoken text the test configures in the shared
    /// answer markers (so the translator's extraction is exercised), and counts clears so a
    /// test can prove the context is reset after every translation.
    /// </summary>
    private sealed class FakeBrain : IAgentBrain
    {
        private readonly Func<string, string> _spokenForPrompt;
        public List<string> Asks { get; } = new();
        public int ClearCount { get; private set; }

        public FakeBrain(Func<string, string> spokenForPrompt) => _spokenForPrompt = spokenForPrompt;

        public string? SessionId => "fake-brain-session";

        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Asks.Add(prompt);
            var spoken = _spokenForPrompt(prompt);
            // Wrap in the shared answer markers the translator extracts between, exactly as
            // a real session is instructed to.
            var wrapped = $"{SessionAskRunner.AnswerBeginMarker}\n{spoken}\n{SessionAskRunner.AnswerEndMarker}";
            return Task.FromResult(new AskResult { Text = wrapped, ReplySeconds = 0.2 });
        }

        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default)
        {
            ClearCount++;
            return Task.FromResult(new ClearResult());
        }
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    private static WingmanTranslator BuildTranslator(FakeBrain brain)
        => new((_, _) => Task.FromResult<IAgentBrain>(brain), log: _ => { });

    [Fact]
    public async Task TranslateAsync_ReturnsTheSpokenTranslation_FromBetweenTheMarkers()
    {
        var brain = new FakeBrain(_ => "The login bug is fixed. All seventy-three tests passed.");
        var translator = BuildTranslator(brain);

        var result = await translator.TranslateAsync(
            "Did the login fix work?",
            "I patched the auth flow in `LoginService.cs` and the suite is green: 73/73.");

        Assert.Equal("The login bug is fixed. All seventy-three tests passed.", result.Spoken);
    }

    [Fact]
    public async Task ModelRole_SummaryUsesFast_TalkToWingmanUsesThinking()
    {
        var brain = new FakeBrain(_ => "ok");
        var roles = new List<WingmanModelRole>();
        var translator = new WingmanTranslator(
            (role, _) =>
            {
                roles.Add(role);
                return Task.FromResult<IAgentBrain>(brain);
            },
            log: _ => { });

        await translator.TranslateAsync("recent context", "the agent reply to translate");
        await translator.AskDirectAsync("hey wingman, what is going on?");

        Assert.Equal(new[] { WingmanModelRole.Fast, WingmanModelRole.Thinking }, roles);
    }

    [Fact]
    public async Task TranslateAsync_ClearsTheContext_AfterEveryTranslation()
    {
        var brain = new FakeBrain(_ => "Done.");
        var translator = BuildTranslator(brain);

        await translator.TranslateAsync("q1", "reply one");
        await translator.TranslateAsync("q2", "reply two");

        // Keep alive, but clear between uses (issue #531): one clear per translation.
        Assert.Equal(2, brain.ClearCount);
    }

    [Fact]
    public async Task TranslateAsync_ClearsTheContext_EvenWhenTheAskThrows()
    {
        var brain = new ThrowingBrain();
        var translator = new WingmanTranslator((_, _) => Task.FromResult<IAgentBrain>(brain), log: _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync("q", "some reply"));

        Assert.Equal(1, brain.ClearCount);
    }

    private sealed class ThrowingBrain : IAgentBrain
    {
        public int ClearCount { get; private set; }
        public string? SessionId => "throwing";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
            => throw new InvalidOperationException("brain blew up");
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default)
        {
            ClearCount++;
            return Task.FromResult(new ClearResult());
        }
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    [Fact]
    public async Task TranslateAsync_CarriesTheAgentReplyVerbatim_IntoThePrompt()
    {
        var brain = new FakeBrain(_ => "ok");
        var translator = BuildTranslator(brain);
        const string reply = "I changed the timeout to 30 seconds and re-ran the failing case.";

        await translator.TranslateAsync("what did you change?", reply);

        var prompt = Assert.Single(brain.Asks);
        Assert.Contains(reply, prompt);
        Assert.Contains("FIDELITY", prompt); // the fidelity contract is present
        Assert.Contains("what did you change?", prompt); // the person's message is present
    }

    [Fact]
    public async Task TranslateAsync_PromptInstructsFocusedSpokenFriendlyNarration()
    {
        // Issue #946: the narration is LISTENED to, so the prompt must tell the wingman to lead with
        // the point (focused) and to describe paths/URLs/symbols in words rather than voicing raw
        // punctuation ("hashtag", "colon slash slash"). This is prompt-only - guard that the rules are
        // present so they cannot silently regress.
        var brain = new FakeBrain(_ => "ok");
        var translator = BuildTranslator(brain);

        await translator.TranslateAsync("q", "I edited file:///D:/repo/x.html");

        var prompt = Assert.Single(brain.Asks);
        Assert.Contains("BE FOCUSED", prompt);
        Assert.Contains("SPEAK FOR THE EAR", prompt);
        Assert.Contains("colon slash slash", prompt); // the concrete "do not voice this" example
    }

    [Fact]
    public async Task TranslateAsync_PromptBansMarkdownSoTtsDoesNotVoiceTheMarks()
    {
        // The real defect: the agent reply is Markdown (**bold**, ## headings, "1." lists) and that
        // string was fed straight to text-to-speech, so the voice read "star star" / "hashtag" out
        // loud. The fix is prompt-only (fix the model output at the source, not with a regex strip):
        // the fidelity contract must explicitly forbid Markdown. Guard the rule so it cannot silently
        // regress.
        var brain = new FakeBrain(_ => "ok");
        var translator = BuildTranslator(brain);

        await translator.TranslateAsync("q", "**BPMN Studio** is option 1. ## Root cause: the panel path.");

        var prompt = Assert.Single(brain.Asks);
        Assert.Contains("NO MARKDOWN", prompt);
        Assert.Contains("asterisks", prompt);          // the exact characters from the bug report
        Assert.Contains("star star BPMN Studio star star", prompt); // the concrete failure example
    }

    [Fact]
    public void BuildDirectPrompt_BansMarkdownForTheEar()
    {
        // The "hey wingman" path is also spoken, so its prompt carries the same no-Markdown rule.
        var prompt = WingmanTranslator.BuildDirectPrompt("what is going on?");
        Assert.Contains("NO Markdown", prompt);
        Assert.Contains("what is going on?", prompt);
    }

    [Fact]
    public void BuildDevThrottlePrompt_BansMarkdownBecauseTheAnswerIsShownRawAndMaySpeak()
    {
        // The Learning-page answer is rendered as raw text (no Markdown renderer) and may be read
        // aloud, so formatting marks would show/voice literally. The prompt must forbid them.
        var prompt = WingmanTranslator.BuildDevThrottlePrompt("How do I start a session?");
        Assert.Contains("NO Markdown", prompt);
    }

    [Fact]
    public async Task TranslateAsync_EmptyReply_ThrowsBecauseThereIsNothingToTranslate()
    {
        var translator = BuildTranslator(new FakeBrain(_ => "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => translator.TranslateAsync("q", "   "));
    }

    [Fact]
    public async Task TranslateAsync_BrainReplyWithoutMarkers_UsesTheWholeReply()
    {
        // An LLM does not always emit the formatting markers. When they are absent the brain's
        // reply IS the spoken answer (it was told to output only that), so we use it rather than
        // 502 - the reliability fix for the explain/voice-turn path.
        var brain = new NoMarkersBrain();   // returns "just some text with no markers at all"
        var translator = new WingmanTranslator((_, _) => Task.FromResult<IAgentBrain>(brain), log: _ => { });
        var result = await translator.TranslateAsync("q", "a reply");
        Assert.Equal("just some text with no markers at all", result.Spoken);
    }

    [Fact]
    public async Task TranslateAsync_RaggedOpeningMarker_IsStrippedNotSpoken()
    {
        // The real leak users saw: the model emitted "===DEVTHROTTLE-ANSWER-BEGIN==" (two trailing
        // equals instead of three). An exact-string match missed it and the ragged marker was
        // spoken/shown at the front of the answer. The tolerant matcher must strip it clean.
        var raggedOpen = SessionAskRunner.AnswerBeginMarker.TrimEnd('=') + "==";      // "...BEGIN=="
        var raggedClose = "==" + SessionAskRunner.AnswerEndMarker.TrimStart('=');     // "==...END==="
        var brain = new FixedReplyBrain($"{raggedOpen}\nThe session started fine.\n{raggedClose}");
        var translator = new WingmanTranslator((_, _) => Task.FromResult<IAgentBrain>(brain), log: _ => { });

        var result = await translator.TranslateAsync("q", "a reply");

        Assert.Equal("The session started fine.", result.Spoken);
        Assert.DoesNotContain("=", result.Spoken);
        Assert.DoesNotContain("DEVTHROTTLE-ANSWER", result.Spoken);
    }

    /// <summary>A brain that returns a fixed, test-supplied reply verbatim (raw markers included).</summary>
    private sealed class FixedReplyBrain : IAgentBrain
    {
        private readonly string _reply;
        public FixedReplyBrain(string reply) => _reply = reply;
        public string? SessionId => "fixed";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(new AskResult { Text = _reply });
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    private sealed class NoMarkersBrain : IAgentBrain
    {
        public string? SessionId => "nomarkers";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(new AskResult { Text = "just some text with no markers at all" });
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    [Fact]
    public async Task AskDirectAsync_AnswersThePersonDirectly_AndClearsContext()
    {
        var brain = new FakeBrain(_ => "I cannot edit files myself, but I can explain what the test does.");
        var translator = BuildTranslator(brain);

        var result = await translator.AskDirectAsync("Hey wingman, what does this test check?");

        Assert.Equal("I cannot edit files myself, but I can explain what the test does.", result.Spoken);
        Assert.Equal(1, brain.ClearCount);
        var prompt = Assert.Single(brain.Asks);
        Assert.Contains("Hey wingman, what does this test check?", prompt);
        Assert.Contains("do NOT edit files", prompt); // the direct-path contract
    }

    [Fact]
    public async Task AskAboutDevThrottleAsync_AnswersTheProductQuestion_AndClearsContext()
    {
        // Issue #472: the Cockpit Learning page Q&A path. The brain is grounded as DevThrottle's
        // in-product help and answers the question; the context is cleared after, like the others.
        var brain = new FakeBrain(_ => "DevThrottle runs and supervises many Claude Code sessions at once.");
        var translator = BuildTranslator(brain);

        var result = await translator.AskAboutDevThrottleAsync("What is DevThrottle?");

        Assert.Equal("DevThrottle runs and supervises many Claude Code sessions at once.", result.Spoken);
        Assert.Equal(1, brain.ClearCount);
        var prompt = Assert.Single(brain.Asks);
        Assert.Contains("What is DevThrottle?", prompt);                 // the user's question is carried
        Assert.Contains("DevThrottle's in-product help", prompt);        // the product grounding is present
    }

    [Fact]
    public async Task AskAboutDevThrottleAsync_EmptyQuestion_Throws()
    {
        var translator = BuildTranslator(new FakeBrain(_ => "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => translator.AskAboutDevThrottleAsync("   "));
    }

    [Fact]
    public void BuildDevThrottlePrompt_GroundsTheBrainAsDevThrottleHelp_AndCarriesTheQuestion()
    {
        var prompt = WingmanTranslator.BuildDevThrottlePrompt("How do I start a session?");

        Assert.Contains("DevThrottle's in-product help", prompt);
        Assert.Contains("Answer ONLY about DevThrottle", prompt);
        Assert.Contains("How do I start a session?", prompt);
        Assert.Contains(SessionAskRunner.AnswerBeginMarker, prompt);
        Assert.Contains(SessionAskRunner.AnswerEndMarker, prompt);
    }

    [Fact]
    public void CleanupForSpeech_StripsCodeFencesButKeepsInlineIdentifierText()
    {
        var input = "Here is the change:\n```csharp\nvar x = 1;\n```\nIt updates `timeoutMs` to thirty seconds.";
        var cleaned = WingmanTranslator.CleanupForSpeech(input);

        Assert.DoesNotContain("```", cleaned);
        Assert.DoesNotContain("var x = 1;", cleaned);
        Assert.Contains("timeoutMs", cleaned); // inline identifier text is the answer's content (issue #368)
    }

    [Fact]
    public void CleanupForSpeech_LeavesNonLatinTextUntouched()
    {
        const string korean = "로그인 버그를 수정했습니다. 모든 테스트가 통과했습니다.";
        Assert.Equal(korean, WingmanTranslator.CleanupForSpeech(korean));
    }

    [Fact]
    public void CleanupForSpeech_StripsBoldAndItalicAsterisks_SoTtsDoesNotSayStarStar()
    {
        // The exact bug: **BPMN Studio** was voiced as "star star BPMN Studio star star". The
        // deterministic pass removes the emphasis wrappers but keeps the words.
        var cleaned = WingmanTranslator.CleanupForSpeech("**BPMN Studio** is the *best* option here.");

        Assert.DoesNotContain("*", cleaned);
        Assert.Contains("BPMN Studio", cleaned);
        Assert.Contains("best", cleaned);
    }

    [Fact]
    public void CleanupForSpeech_StripsHeadingHashMarks_KeepingTheHeadingWords()
    {
        var cleaned = WingmanTranslator.CleanupForSpeech("## Root cause\nThe panel path was wrong.");

        Assert.DoesNotContain("#", cleaned);
        Assert.Contains("Root cause", cleaned);
        Assert.Contains("The panel path was wrong.", cleaned);
    }

    [Fact]
    public void CleanupForSpeech_StripsBulletAndNumberedListMarkers()
    {
        var input = "Here is the plan:\n- First, patch the auth flow.\n- Then rerun the tests.\n1. Build.\n2) Ship.";
        var cleaned = WingmanTranslator.CleanupForSpeech(input);

        Assert.Contains("First, patch the auth flow.", cleaned);
        Assert.Contains("Then rerun the tests.", cleaned);
        Assert.Contains("Build.", cleaned);
        Assert.Contains("Ship.", cleaned);
        // No line still begins with a bullet or list marker.
        foreach (var line in cleaned.Split('\n'))
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*(?:[-*+]\s|\d+[.)]\s)"),
                $"A list marker survived: '{line}'");
    }

    [Fact]
    public void CleanupForSpeech_KeepsUnderscoresInsideIdentifiers_ButStripsItalicUnderscores()
    {
        // snake_case is an identifier - its underscores are content and must survive. A word wrapped
        // in underscores for italics (_emphasis_) is a mark and must go.
        var cleaned = WingmanTranslator.CleanupForSpeech("The _urgent_ fix touches snake_case_name.");

        Assert.Contains("snake_case_name", cleaned); // identifier underscores preserved
        Assert.Contains("urgent", cleaned);
        Assert.DoesNotContain("_urgent_", cleaned);   // italic wrapper removed
    }

    [Fact]
    public void CleanupForSpeech_StripsMarkdownLinks_KeepingTheLinkText()
    {
        var cleaned = WingmanTranslator.CleanupForSpeech("See [the release notes](https://example.com/notes) for details.");

        Assert.Contains("the release notes", cleaned);
        Assert.DoesNotContain("https://example.com/notes", cleaned);
        Assert.DoesNotContain("](", cleaned);
    }

    [Fact]
    public void CleanupForSpeech_LeavesNumbersUntouched_FidelityIsNotChanged()
    {
        // The pass changes how text is spoken, never the facts. Numbers - including long ones - are
        // the answer's content and must pass through exactly.
        const string input = "All 73 tests passed and the id is 1204987654321.";
        var cleaned = WingmanTranslator.CleanupForSpeech(input);

        Assert.Contains("73", cleaned);
        Assert.Contains("1204987654321", cleaned);
    }

    [Fact]
    public void CleanupForSpeech_ReplyLikeTheBugReport_ComesOutFullyClean()
    {
        // A representative Markdown-heavy reply of the kind the Fast tier still leaks despite the
        // prompt. After the pass, none of the formatting characters that get voiced literally remain.
        const string input =
            "## Summary\n" +
            "**BPMN Studio** is option 1. Use the `PanelPath` setting.\n" +
            "- Fix the `auth` flow\n" +
            "1. Run `dotnet test`\n" +
            "See [the docs](https://example.com).\n" +
            "| Col A | Col B |\n| --- | --- |\n| x | y |";
        var cleaned = WingmanTranslator.CleanupForSpeech(input);

        Assert.DoesNotContain("*", cleaned);
        Assert.DoesNotContain("#", cleaned);
        Assert.DoesNotContain("`", cleaned);
        Assert.DoesNotContain("|", cleaned);
        Assert.DoesNotContain("](", cleaned);
        // The words survive.
        Assert.Contains("BPMN Studio", cleaned);
        Assert.Contains("PanelPath", cleaned);
        Assert.Contains("the docs", cleaned);
    }

    /// <summary>
    /// Proves the only dependency of a translation is a real-session <see cref="IAgentBrain"/>.
    /// The whole pipeline runs to completion against a pure in-memory fake - no process is
    /// spawned, no <c>--print</c> CLI is invoked. The brain is configured elsewhere (issues
    /// #509/#510) to be a real session, never a metered print call (issue #511).
    /// </summary>
    [Fact]
    public async Task TranslateAsync_RunsEntirelyOverTheBrainSeam_NoPrintProcess()
    {
        var brain = new FakeBrain(_ => "All set.");
        var translator = BuildTranslator(brain);

        var result = await translator.TranslateAsync("status?", "Everything is committed and pushed.");

        Assert.Equal("All set.", result.Spoken);
        Assert.Single(brain.Asks);
    }

    /// <summary>
    /// Emits the HTML QA report (issue #531 proof target): each canned agent reply beside the
    /// wingman's spoken translation, so a human can judge fidelity and speakability at a
    /// glance. Here the spoken side is produced by a fake brain that echoes a representative
    /// short form, which proves the report format and the pipeline; a live capture against the
    /// real configured wingman reuses the same <see cref="WingmanQaReport"/> renderer.
    /// </summary>
    [Fact]
    public async Task Emits_WingmanText_QaReport_Html()
    {
        var fixtures = WingmanQaFixtures.All;
        // A stand-in "good" translation per fixture so the report renders end-to-end offline.
        var brain = new FakeBrain(prompt =>
        {
            foreach (var f in fixtures)
                if (prompt.Contains(f.AgentReply, StringComparison.Ordinal))
                    return f.ExpectedSpokenStandIn;
            return "(no match)";
        });
        var translator = BuildTranslator(brain);

        var rows = new List<WingmanQaRow>();
        foreach (var f in fixtures)
        {
            var r = await translator.TranslateAsync(f.UserMessage, f.AgentReply);
            rows.Add(new WingmanQaRow
            {
                Label = f.Label,
                UserMessage = f.UserMessage,
                AgentReply = f.AgentReply,
                Spoken = r.Spoken,
                ReplySeconds = r.ReplySeconds,
                SpokenChars = r.Spoken.Length,
            });

            // Speakability bound: a spoken turn for a back-and-forth must not balloon. The
            // fidelity prompt allows as many sentences as needed, but a translation many times
            // longer than the agent's own reply means it is not summarising - flag it.
            Assert.True(r.Spoken.Length <= Math.Max(600, f.AgentReply.Length),
                $"Fixture '{f.Label}' produced an over-long spoken translation ({r.Spoken.Length} chars).");
            Assert.DoesNotContain("```", r.Spoken); // never read code fences aloud
            Assert.False(string.IsNullOrWhiteSpace(r.Spoken)); // a non-empty reply yields a non-empty translation
        }

        // Proof generation is OPT-IN, like the sibling proof writers (CC274_PROOF_DIR,
        // CC300_PROOF_DIR, CC1080_PROOF_DIR): a normal `dotnet test` writes nothing. This used
        // to write docs/proof/issue-531/wingman-text-qa.html unconditionally, rewriting a
        // git-tracked file on every Gateway run and leaving every worktree dirty. The
        // assertions above are the test; the report is an artefact the proof run collects.
        var outDir = Environment.GetEnvironmentVariable("CC531_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(outDir)) return;

        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "wingman-text-qa.html");
        File.WriteAllText(outPath, WingmanQaReport.Render(rows, live: false), Encoding.UTF8);

        _out.WriteLine($"QA report written: {outPath}");
        Assert.True(File.Exists(outPath));
    }
}
