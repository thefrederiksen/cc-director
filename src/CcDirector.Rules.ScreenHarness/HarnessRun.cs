using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Rules;

namespace CcDirector.Rules.ScreenHarness;

/// <summary>The answers the report reads off a recorded firing. A closed set, so the summary counts names.</summary>
public static class CaseAnswers
{
    /// <summary>The rule decided to act and the engine let it through (in dry run, so nothing was typed).</summary>
    public const string Act = "act";

    /// <summary>The rule read the screen against the instruction and declined.</summary>
    public const string Decline = "decline";

    /// <summary>The model said act and the engine refused it for citing text the screen does not
    /// contain. The MODEL was still wrong, so this counts against it.</summary>
    public const string ActUngrounded = "act (ungrounded)";

    /// <summary>A staked check failed and the act was given up.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The model could not be asked: the environment logged an exception.</summary>
    public const string NoAnswer = "no answer";

    /// <summary>The model answered and the answer was not one - it named something it was not offered.</summary>
    public const string Refused = "refused";

    /// <summary>The free checks never let the model see the screen. A corpus defect.</summary>
    public const string NotAsked = "not asked";

    /// <summary>Whether an answer is an act in either form - the two answers that count as wrong on a negative.</summary>
    public static bool IsAnAct(string answer) =>
        string.Equals(answer, Act, StringComparison.Ordinal) || string.Equals(answer, ActUngrounded, StringComparison.Ordinal);

    /// <summary>The answer given, read off the pass and the environment. See the phase 0 task for the table.</summary>
    public static string For(RulePass pass, CaseRuleEnvironment environment) => pass.What switch
    {
        RulePassOutcomes.DryRun => Act,
        RulePassOutcomes.Declined => Decline,
        RulePassOutcomes.Ungrounded => ActUngrounded,
        RulePassOutcomes.Abandoned => Abandoned,
        RulePassOutcomes.Refused => environment.ModelFailure is not null ? NoAnswer : Refused,
        RulePassOutcomes.NoCandidates => NotAsked,
        RulePassOutcomes.StoppedBeforeAnyRule => NotAsked,
        _ => pass.What,
    };
}

/// <summary>One row of the report: one case on one model.</summary>
public sealed record CaseResult(
    string Model,
    string CaseId,
    string Kind,
    string Expected,
    string? ExpectedRuleId,
    string Answer,
    bool Right,
    double? Seconds,
    string Outcome,
    string Detail,
    string? FiringRuleId,
    bool TimedOut,
    string? Failure,
    IReadOnlyList<RuleFiringDraft> Firings);

/// <summary>The numbers the phase is judged on, per model.</summary>
public sealed record ModelSummary(
    string Model,
    int Cases,
    int WrongOnNegatives,
    int WrongOnNegativesThatReachedAct,
    int WrongOnNegativesStoppedByGrounding,
    int Timeouts,
    int OtherNoAnswers,
    int WrongOnPositives,
    IReadOnlyList<string> NotAsked,
    int Right,
    int Wrong,
    double? MedianSeconds,
    double? MaximumSeconds);

/// <summary>What one run was asked to do.</summary>
public sealed record HarnessOptions(
    IReadOnlyList<string> ModelNames,
    string CorpusDirectory,
    string OutputDirectory,
    string? OnlyCase);

/// <summary>
/// THE RUN: every case through the real <see cref="RuleEvaluator"/> on every model named, cases one at a
/// time within a model so each timing is one call under no self-inflicted load, the models side by side.
/// Then the report, with the number the phase is judged on above everything else.
/// </summary>
public static class HarnessRun
{
    /// <summary>The two models the runner knows, by the name given on the command line.</summary>
    public static IncludedModelId ModelNamed(string name) => name switch
    {
        "wingman" => IncludedModelId.Wingman,
        "wingman-fast" => IncludedModelId.WingmanFast,
        _ => throw new ArgumentException(
            "unknown model '" + name + "'. The models are named by their included id: wingman, wingman-fast.", nameof(name)),
    };

    /// <summary>Every model name the runner accepts, in the default order.</summary>
    public static readonly string[] DefaultModels = { "wingman", "wingman-fast" };

    /// <summary>Run it. Returns the exit code: 0 only when no case was "not asked" and no negative was
    /// answered with an act, in either form, on any model.</summary>
    /// <exception cref="InvalidOperationException">The DevThrottle account key is not in the vault.</exception>
    public static async Task<int> RunAsync(HarnessOptions options, TextWriter console, CancellationToken ct)
    {
        var models = options.ModelNames.Select(n => (Name: n, Id: ModelNamed(n))).ToList();

        var rules = ScreenCorpus.ReadRules(options.CorpusDirectory);
        var cases = ScreenCorpus.ReadCases(options.CorpusDirectory);
        if (options.OnlyCase is not null)
        {
            cases = cases.Where(c => string.Equals(c.Id, options.OnlyCase, StringComparison.Ordinal)).ToList();
            if (cases.Count == 0)
                throw new InvalidOperationException("no case named '" + options.OnlyCase + "' in " + options.CorpusDirectory);
        }
        if (cases.Count == 0)
            throw new InvalidOperationException("the corpus at " + options.CorpusDirectory + " has no cases, so there is nothing to run.");

        // THE SAME VAULT THE GATEWAY READS, read once, and a missing key stops the run before any case:
        // a run that reached the model with no credential would answer "no answer" on every case and
        // look like a model problem.
        //
        // The vault lives under the storage root, and the root moves with CC_DIRECTOR_ROOT - a fleet session
        // runs with it pointed at an instance directory whose vault is not the installed Gateway's. The
        // message names the root it looked under, so that redirect is the first thing a reader sees.
        var key = new KeyVault().Get(TranscriptionEndpointResolver.DevThrottleKeyName);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "the vault under the storage root " + CcStorage.Root() + " has no " +
                TranscriptionEndpointResolver.DevThrottleKeyName + ". Sign in to DevThrottle on this machine so " +
                "the Gateway's own key is there, or clear CC_DIRECTOR_ROOT if it points this run at an instance " +
                "directory that is not the Gateway's, then run again.");

        Directory.CreateDirectory(options.OutputDirectory);
        console.WriteLine("screen harness: " + cases.Count + " case(s), " + rules.Count + " rule(s), models: " +
                          string.Join(", ", models.Select(m => m.Name)));
        console.WriteLine("corpus: " + options.CorpusDirectory);
        console.WriteLine("output: " + options.OutputDirectory);

        var lockObject = new object();
        void Log(string line)
        {
            lock (lockObject) console.WriteLine(line);
        }

        var perModel = await Task.WhenAll(models.Select(m => RunModelAsync(m.Name, m.Id, rules, cases, key, Log, ct)))
            .ConfigureAwait(false);

        var rows = perModel.SelectMany(r => r).ToList();
        var summaries = models.Select(m => Summarise(m.Name, rows.Where(r => r.Model == m.Name).ToList())).ToList();

        var report = RenderReport(summaries, rows);
        // UTF-8 without a marker: the harness's own words are ASCII, but a detail may quote a captured
        // screen, and a screen that carried other bytes is reported as it was rather than with them replaced.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "report.md"), report, utf8, ct)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(options.OutputDirectory, "results.json"),
            JsonSerializer.Serialize(new { summaries, rows }, ResultsJson), utf8, ct).ConfigureAwait(false);

        console.WriteLine();
        console.WriteLine(RenderSummaries(summaries));
        console.WriteLine("report: " + Path.Combine(options.OutputDirectory, "report.md"));

        var exitCode = summaries.All(s => s.NotAsked.Count == 0 && s.WrongOnNegatives == 0) ? 0 : 1;
        console.WriteLine("exit code " + exitCode + (exitCode == 0
            ? ": no negative was answered with an act and every case was asked."
            : ": a negative was answered with an act, or a case was never asked. Read the report."));
        return exitCode;
    }

    private static async Task<List<CaseResult>> RunModelAsync(
        string modelName,
        IncludedModelId model,
        IReadOnlyList<SessionRule> rules,
        IReadOnlyList<ScreenCase> cases,
        string key,
        Action<string> log,
        CancellationToken ct)
    {
        var results = new List<CaseResult>();
        foreach (var screenCase in cases)
        {
            ct.ThrowIfCancellationRequested();

            // A FRESH EVALUATOR AND A FRESH ENVIRONMENT PER CASE, with the case id as the session id, so
            // nothing the evaluator remembers about one screen can reach the next.
            var environment = new CaseRuleEnvironment(rules, screenCase, model, key, log);
            var evaluator = new RuleEvaluator(environment);

            var pass = await evaluator.EvaluateAsync(TenantId.Local, "screen-harness", screenCase.Id, ct)
                .ConfigureAwait(false);

            var result = Judge(modelName, screenCase, pass, environment);
            results.Add(result);
            log("[" + modelName + "] " + result.CaseId + " (" + result.Kind + "): expected " + result.Expected +
                ", answered " + result.Answer + " - " + (result.Right ? "RIGHT" : "WRONG") +
                (result.Seconds is null ? "" : " in " + result.Seconds.Value.ToString("F1", CultureInfo.InvariantCulture) + "s") +
                " [" + result.Outcome + "]");
        }
        return results;
    }

    /// <summary>Read the verdict off the pass and the environment: what was answered, and whether that is
    /// what the case says is right.</summary>
    public static CaseResult Judge(string modelName, ScreenCase screenCase, RulePass pass, CaseRuleEnvironment environment)
    {
        var answer = CaseAnswers.For(pass, environment);
        var firings = environment.Firings.Select(f => f.Draft).ToList();
        var firingRuleId = pass.Recorded.Count > 0 ? pass.Recorded[0].RuleId.ToString() : null;
        var expected = screenCase.Record.Expected;

        var right = expected switch
        {
            CaseExpectations.Act =>
                answer == CaseAnswers.Act
                && firingRuleId is not null
                && string.Equals(firingRuleId, screenCase.Record.ExpectedRuleId, StringComparison.OrdinalIgnoreCase),
            CaseExpectations.Decline => answer == CaseAnswers.Decline,
            _ => false,
        };

        var failure = environment.ModelFailure;
        return new CaseResult(
            Model: modelName,
            CaseId: screenCase.Id,
            Kind: screenCase.Record.Kind,
            Expected: expected,
            ExpectedRuleId: screenCase.Record.ExpectedRuleId,
            Answer: answer,
            Right: right,
            Seconds: environment.ModelCallTime?.TotalSeconds,
            Outcome: pass.What,
            Detail: pass.Detail,
            FiringRuleId: firingRuleId,
            TimedOut: failure is TimeoutException,
            Failure: failure is null ? null : failure.GetType().Name + ": " + failure.Message,
            Firings: firings);
    }

    /// <summary>The numbers for one model.</summary>
    public static ModelSummary Summarise(string modelName, IReadOnlyList<CaseResult> rows)
    {
        var negatives = rows.Where(r => r.Expected == CaseExpectations.Decline).ToList();
        var positives = rows.Where(r => r.Expected == CaseExpectations.Act).ToList();
        var wrongNegatives = negatives.Where(r => CaseAnswers.IsAnAct(r.Answer)).ToList();
        var times = rows.Where(r => r.Seconds is not null).Select(r => r.Seconds!.Value).OrderBy(s => s).ToList();

        return new ModelSummary(
            Model: modelName,
            Cases: rows.Count,
            WrongOnNegatives: wrongNegatives.Count,
            WrongOnNegativesThatReachedAct: wrongNegatives.Count(r => r.Answer == CaseAnswers.Act),
            WrongOnNegativesStoppedByGrounding: wrongNegatives.Count(r => r.Answer == CaseAnswers.ActUngrounded),
            Timeouts: rows.Count(r => r.TimedOut),
            OtherNoAnswers: rows.Count(r => r.Answer == CaseAnswers.NoAnswer && !r.TimedOut),
            WrongOnPositives: positives.Count(r => !r.Right),
            NotAsked: rows.Where(r => r.Answer == CaseAnswers.NotAsked).Select(r => r.CaseId).ToList(),
            Right: rows.Count(r => r.Right),
            Wrong: rows.Count(r => !r.Right),
            MedianSeconds: Median(times),
            MaximumSeconds: times.Count == 0 ? null : times[^1]);
    }

    private static double? Median(List<double> sorted)
    {
        if (sorted.Count == 0) return null;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>The summaries, as the console prints them and the report leads with them.</summary>
    public static string RenderSummaries(IReadOnlyList<ModelSummary> summaries)
    {
        var sb = new StringBuilder();
        foreach (var s in summaries)
        {
            sb.AppendLine("## " + s.Model + " - the numbers");
            sb.AppendLine();
            sb.AppendLine("- WRONG ANSWERS ON NEGATIVES: " + s.WrongOnNegatives + " (the number the phase is judged on)");
            sb.AppendLine("  - of those, reached act (would have typed): " + s.WrongOnNegativesThatReachedAct);
            sb.AppendLine("  - of those, act (ungrounded) - the grounding check stopped it: " + s.WrongOnNegativesStoppedByGrounding);
            sb.AppendLine("- timeouts: " + s.Timeouts + "; other no-answers: " + s.OtherNoAnswers);
            sb.AppendLine("- wrong answers on positives: " + s.WrongOnPositives);
            sb.AppendLine("- cases not asked (a corpus defect): " + s.NotAsked.Count +
                          (s.NotAsked.Count == 0 ? "" : " - " + string.Join(", ", s.NotAsked)));
            sb.AppendLine("- right: " + s.Right + "; wrong: " + s.Wrong + "; cases: " + s.Cases);
            sb.AppendLine("- model call time: median " + Seconds(s.MedianSeconds) + ", maximum " + Seconds(s.MaximumSeconds));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The whole report: the numbers per model first, then one table per model, one row per case.</summary>
    public static string RenderReport(IReadOnlyList<ModelSummary> summaries, IReadOnlyList<CaseResult> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Screen harness report");
        sb.AppendLine();
        sb.AppendLine("Run at " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                      " UTC. Every case went through the real RuleEvaluator; the answer is read off the recorded firing.");
        sb.AppendLine();
        sb.Append(RenderSummaries(summaries));

        foreach (var s in summaries)
        {
            sb.AppendLine("## " + s.Model + " - per case");
            sb.AppendLine();
            sb.AppendLine("| case | kind | expected | answer | right | seconds | outcome |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var r in rows.Where(r => r.Model == s.Model))
            {
                sb.AppendLine("| " + Cell(r.CaseId) + " | " + Cell(r.Kind) + " | " + Cell(r.Expected) + " | " +
                              Cell(r.Answer) + " | " + (r.Right ? "right" : "WRONG") + " | " +
                              (r.Seconds is null ? "" : r.Seconds.Value.ToString("F1", CultureInfo.InvariantCulture)) +
                              " | " + Cell(r.Outcome + ": " + Shorten(r.Failure ?? r.Detail)) + " |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Seconds(double? value) =>
        value is null ? "n/a" : value.Value.ToString("F1", CultureInfo.InvariantCulture) + "s";

    private static string Cell(string text) => text.Replace("|", "\\|").ReplaceLineEndings(" ");

    /// <summary>The detail, short enough to read in a table cell.</summary>
    public static string Shorten(string? detail)
    {
        var text = (detail ?? "").Trim().ReplaceLineEndings(" ");
        return text.Length <= 160 ? text : text[..160] + "...";
    }

    private static readonly JsonSerializerOptions ResultsJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
