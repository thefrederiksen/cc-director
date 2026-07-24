using CcDirector.AgentBrain;
using CcDirector.Core.Dictation.Models;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The model screening pass (devthrottle #2115): the prompt carries every candidate with its evidence and
/// demands a strict JSON verdict array; the parser accepts exactly that (tolerating a fenced wrapper) and
/// THROWS on anything unusable - an unparseable answer or a candidate left unjudged - because the caller
/// must record "screening unavailable" rather than guess.
/// </summary>
public sealed class DictionarySuggestionScreenTests
{
    private static MistranscriptionSuggestion Candidate(string term, params string[] heard)
        => new(term, heard.Select(v => new MistranscriptionVariant(v, 2)).ToList(), heard.Length * 2, heard.Length * 2 + 5);

    private sealed class FixedBrain : IAgentBrain
    {
        private readonly string _text;
        public string? LastPrompt;
        public FixedBrain(string text) => _text = text;
        public string? SessionId => null;
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(new AskResult { Text = _text });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    [Fact]
    public void BuildPrompt_CarriesEveryCandidateAndItsEvidence()
    {
        var prompt = DictionarySuggestionScreen.BuildPrompt(new[]
        {
            Candidate("mindzie", "Mindsee", "Mindzee"),
            Candidate("ConPty", "Con-TY"),
        });

        Assert.Contains("1. \"mindzie\" heard as: Mindsee, Mindzee", prompt);
        Assert.Contains("2. \"ConPty\" heard as: Con-TY", prompt);
        // The judgment framing: ordinary words in ANY language are rejected; unsure means reject.
        Assert.Contains("ANY language", prompt);
        Assert.Contains("When unsure, REJECT", prompt);
        Assert.Contains("ONLY a JSON array", prompt);
    }

    [Fact]
    public async Task JudgeAsync_MapsVerdictsBackInCandidateOrder()
    {
        var brain = new FixedBrain(
            """
            [{"term":"ConPty","approved":true,"reason":"jargon"},
             {"term":"that","approved":false,"reason":"ordinary word"}]
            """);
        var verdicts = await DictionarySuggestionScreen.JudgeAsync(brain, new[]
        {
            Candidate("that", "then", "them"),
            Candidate("ConPty", "Con-TY"),
        });

        Assert.Equal(2, verdicts.Count);
        Assert.Equal("that", verdicts[0].Term);
        Assert.False(verdicts[0].Approved);
        Assert.Equal("ordinary word", verdicts[0].Reason);
        Assert.Equal("ConPty", verdicts[1].Term);
        Assert.True(verdicts[1].Approved);
    }

    [Fact]
    public void ParseVerdicts_ToleratesAFencedCodeBlock()
    {
        var text = "```json\n[{\"term\":\"mindzie\",\"approved\":true,\"reason\":\"brand\"}]\n```";
        var verdicts = DictionarySuggestionScreen.ParseVerdicts(text, new[] { Candidate("mindzie", "Mindsee") });
        Assert.True(Assert.Single(verdicts).Approved);
    }

    [Fact]
    public void ParseVerdicts_MatchesTermsCaseInsensitively()
    {
        var text = "[{\"term\":\"MINDZIE\",\"approved\":true,\"reason\":\"brand\"}]";
        var verdicts = DictionarySuggestionScreen.ParseVerdicts(text, new[] { Candidate("mindzie", "Mindsee") });
        Assert.Equal("mindzie", Assert.Single(verdicts).Term); // the CANDIDATE's spelling, not the model's
    }

    [Fact]
    public void ParseVerdicts_ProseAnswer_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DictionarySuggestionScreen.ParseVerdicts(
                "These all look like fine dictionary terms to me.",
                new[] { Candidate("mindzie", "Mindsee") }));
        Assert.Contains("no JSON array", ex.Message);
    }

    [Fact]
    public void ParseVerdicts_MalformedJson_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DictionarySuggestionScreen.ParseVerdicts(
                "[{\"term\":\"mindzie\",\"approved\":tr",
                new[] { Candidate("mindzie", "Mindsee") }));
    }

    [Fact]
    public void ParseVerdicts_MissingCandidate_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DictionarySuggestionScreen.ParseVerdicts(
                "[{\"term\":\"mindzie\",\"approved\":true,\"reason\":\"brand\"}]",
                new[] { Candidate("mindzie", "Mindsee"), Candidate("ConPty", "Con-TY") }));
        Assert.Contains("ConPty", ex.Message);
    }

    [Fact]
    public async Task JudgeAsync_EmptyCandidates_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DictionarySuggestionScreen.JudgeAsync(new FixedBrain("[]"), Array.Empty<MistranscriptionSuggestion>()));
    }
}
