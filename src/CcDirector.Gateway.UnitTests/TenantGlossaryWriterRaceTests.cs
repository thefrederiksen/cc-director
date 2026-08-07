using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A CONCURRENT HUMAN EDIT MUST SURVIVE AN AGENT ADD, AND VICE VERSA - the invariant the owner's ruling for
/// issue #2484 rests on, proven by actually racing two writers rather than describing them.
///
/// THE DEFECT THESE TESTS PIN. Every glossary writer used to do an unguarded read-modify-write, so a
/// person's Cockpit save landing between another caller's read and write was erased - wrong-spellings list
/// and all - and two concurrent additions could lose one of the terms. That makes the sentence the whole
/// grant was justified by ("worst case is a stray extra word, never the loss of a correction the owner
/// relies on") FALSE: add-only would be true of the verb and false of the effect.
///
/// WHY A SERIAL TEST CANNOT SEE THIS, WHICH IS EXACTLY WHY IT SURVIVED THE FIRST SUITE. Run one writer then
/// the other and every version of this code passes, including the broken one - the window only exists when
/// a second writer is inside the first one's read-modify-write. So these tests start both writers together
/// and repeat the race enough times to hit the window.
///
/// WHAT IS ASSERTED IS LINEARIZABILITY, NOT A FIXED ANSWER. Two legal outcomes exist when a person's whole
/// document save races an agent's add, and which one wins is a genuine race: the person's save may
/// legitimately overwrite the agent's term (an explicit human save is authoritative - pruning is what the
/// person is FOR), and the agent's term may legitimately land on top of the save. What may never happen is
/// the torn middle, where the agent's word is kept and the person's curation is dropped, because that is
/// half of each write and no ordering at all. Asserting one fixed winner would be asserting a race.
/// </summary>
public sealed class TenantGlossaryWriterRaceTests : IDisposable
{
    private const int Rounds = 60;

    private readonly string _root;
    private readonly string? _priorRoot;

    public TenantGlossaryWriterRaceTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "cc-glossary-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A distinct tenant per round, so one round's file cannot carry state into the next and turn a
    /// lost update into a pass.</summary>
    private static TenantId TenantFor(int round) => new($"race-tenant-{round}-{Guid.NewGuid():N}");

    /// <summary>The person's curated document: a term with a wrong-spellings list they just edited. Losing
    /// that list is the exact harm the ruling forbids.</summary>
    private static DictationDictionary PersonsCuration() => new(
        new List<string> { "Frederiksen" },
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Frederiksen"] = new List<string> { "Fredrickson", "Fredriksson" },
        },
        new Dictionary<string, DictationProfile>(StringComparer.Ordinal));

    /// <summary>Release both threads at the same instant. Starting them sequentially would usually let the
    /// first finish before the second began, which is a serial test wearing a race's clothes.</summary>
    private static void RaceTwo(Action first, Action second)
    {
        using var gate = new ManualResetEventSlim(false);
        Exception? failure = null;

        void Run(Action action)
        {
            try { gate.Wait(); action(); }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
        }

        var a = new Thread(() => Run(first));
        var b = new Thread(() => Run(second));
        a.Start();
        b.Start();
        gate.Set();
        a.Join();
        b.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException($"A racing writer threw: {failure}");
    }

    [Fact]
    public void A_persons_save_racing_an_agent_add_is_never_half_lost()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = TenantFor(round);

            RaceTwo(
                // The person saves their curated document from the Cockpit editor.
                () => TenantGlossaryWriter.Replace(tenant, PersonsCuration()),
                // An agent adds a word at the same moment.
                () => TenantGlossary.AddTerms(
                    tenant,
                    new[] { "Kubernetes" },
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)));

            var final = TenantGlossary.Load(tenant);

            // Both orderings keep the person's term AND its wrong spellings. The broken code produced a
            // document holding "Kubernetes" with the person's list gone - half of each write.
            Assert.Contains("Frederiksen", final.Vocabulary);
            Assert.True(final.CommonMistranscriptions.ContainsKey("Frederiksen"),
                $"round {round}: the person's wrong-spellings list was ERASED by a concurrent agent add - " +
                "this is the loss the owner's ruling forbids");
            Assert.Equal(
                new[] { "Fredrickson", "Fredriksson" },
                final.CommonMistranscriptions["Frederiksen"].ToArray());

            // And the result is one of the two legal serial orderings, never a third thing.
            var agentWordSurvived = final.Vocabulary.Contains("Kubernetes");
            var legal = agentWordSurvived
                ? final.Vocabulary.Count == 2   // person's save, then the add landed on top
                : final.Vocabulary.Count == 1;  // the add, then the person's save replaced it - also legal
            Assert.True(legal, $"round {round}: final vocabulary is no serial ordering: [{string.Join(", ", final.Vocabulary)}]");
        }
    }

    [Fact]
    public void Two_concurrent_agent_adds_both_survive()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = TenantFor(round);
            var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            RaceTwo(
                () => TenantGlossary.AddTerms(tenant, new[] { "Kubernetes" }, empty),
                () => TenantGlossary.AddTerms(tenant, new[] { "Helm" }, empty));

            var final = TenantGlossary.Load(tenant);

            // Neither add is a replace, so there is no ordering in which one is lost. Unguarded, the second
            // writer's stale copy overwrote the first's word.
            Assert.Contains("Kubernetes", final.Vocabulary);
            Assert.Contains("Helm", final.Vocabulary);
            Assert.Equal(2, final.Vocabulary.Count);
        }
    }

    [Fact]
    public void A_wrong_spellings_edit_racing_an_add_keeps_both()
    {
        // The suggestion-apply path writes a term AND its wrong spellings, and an agent add races it. Both
        // are additive, so both must be present afterwards - this is the writer that used to share one
        // <path>.tmp staging file with everything else.
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = TenantFor(round);

            RaceTwo(
                () => TenantGlossary.AddTerms(
                    tenant,
                    new[] { "mindzie" },
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                    {
                        ["mindzie"] = new List<string> { "Mindsee" },
                    }),
                () => TenantGlossary.AddTerms(
                    tenant,
                    new[] { "Kubernetes" },
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)));

            var final = TenantGlossary.Load(tenant);

            Assert.Contains("mindzie", final.Vocabulary);
            Assert.Contains("Kubernetes", final.Vocabulary);
            Assert.True(final.CommonMistranscriptions.ContainsKey("mindzie"), $"round {round}: wrong spellings lost");
            Assert.Contains("Mindsee", final.CommonMistranscriptions["mindzie"]);
        }
    }

    [Fact]
    public void Many_concurrent_adds_lose_nothing()
    {
        // Two writers can miss a narrow window by luck. Eight at once, repeatedly, is the version that fails
        // loudly on an unguarded read-modify-write.
        const int writers = 8;
        for (var round = 0; round < 15; round++)
        {
            var tenant = TenantFor(round);
            var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            using var gate = new ManualResetEventSlim(false);
            Exception? failure = null;

            // Every writer's exception is CAUGHT and reported. An exception escaping a raw thread kills the
            // whole test host - the run aborts instead of failing, which hides every test after it and
            // reports nothing useful about this one. (The unguarded version really does throw here: two
            // writers collide on the shared <path>.tmp staging file.)
            var threads = Enumerable.Range(0, writers).Select(i => new Thread(() =>
            {
                try
                {
                    gate.Wait();
                    TenantGlossary.AddTerms(tenant, new[] { $"term-{i}" }, empty);
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            })).ToList();

            foreach (var t in threads) t.Start();
            gate.Set();
            foreach (var t in threads) t.Join();

            if (failure is not null)
                throw new Xunit.Sdk.XunitException($"round {round}: a concurrent writer threw: {failure}");

            var final = TenantGlossary.Load(tenant);
            for (var i = 0; i < writers; i++)
                Assert.True(final.Vocabulary.Contains($"term-{i}"),
                    $"round {round}: term-{i} was lost - {writers} concurrent adds produced {final.Vocabulary.Count}");
        }
    }
}
