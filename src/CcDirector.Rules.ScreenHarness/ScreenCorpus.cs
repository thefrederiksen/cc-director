using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Rules;

namespace CcDirector.Rules.ScreenHarness;

/// <summary>The facts a case states about the session its screen came from - the roster row, as fixed data.</summary>
public sealed class CaseFacts
{
    public string Agent { get; set; } = "";
    public string RepositoryPath { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Mission { get; set; } = "";
    public string ActivityState { get; set; } = "";
}

/// <summary>Where a case's screen came from, so a reader can go back to it.</summary>
public sealed class CaseSource
{
    public string Method { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string CapturedUtc { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>The written-down side of one case: the expected answer, why, and where the screen came from.</summary>
public sealed class CaseRecord
{
    public string Id { get; set; } = "";
    public string Expected { get; set; } = "";
    public string? ExpectedRuleId { get; set; }
    public string Kind { get; set; } = "";
    public string Reason { get; set; } = "";
    public CaseFacts Facts { get; set; } = new();
    public string FactsNote { get; set; } = "";
    public CaseSource Source { get; set; } = new();
    public bool NonAscii { get; set; }
    public bool SecretsChecked { get; set; }
}

/// <summary>One case as read off disk: its record, its screen as rows, and the bytes the screen came from.</summary>
public sealed record ScreenCase(
    string Directory,
    CaseRecord Record,
    IReadOnlyList<string> ScreenRows,
    byte[] ScreenBytes)
{
    /// <summary>The case id, which is also the directory name.</summary>
    public string Id => Record.Id;

    /// <summary>Whether the written-down answer is a decline - every negative kind expects one.</summary>
    public bool ExpectsDecline => string.Equals(Record.Expected, CaseExpectations.Decline, StringComparison.Ordinal);

    /// <summary>The facts as the engine reads them, with the case id standing in for the session id so the
    /// evaluator's per-session memory never carries from one case to the next.</summary>
    public RuleSessionFacts SessionFacts() => new(
        SessionId: Record.Id,
        Agent: Record.Facts.Agent,
        RepositoryPath: Record.Facts.RepositoryPath,
        Machine: Record.Facts.Machine,
        Mission: Record.Facts.Mission,
        ActivityState: Record.Facts.ActivityState);
}

/// <summary>The two answers a case may expect.</summary>
public static class CaseExpectations
{
    public const string Act = "act";
    public const string Decline = "decline";
}

/// <summary>The kinds a case may be. Every negative kind expects a decline; the positive kind expects an act.</summary>
public static class CaseKinds
{
    public const string Positive = "positive";
    public const string NegativeDocumentation = "negative-documentation";
    public const string NegativeCode = "negative-code";
    public const string NegativeReport = "negative-report";
    public const string NegativeOwnStateDifferentSituation = "negative-own-state-different-situation";
    public const string NegativeSubstring = "negative-substring";

    /// <summary>Every kind, in the order the format lists them.</summary>
    public static readonly string[] All =
    {
        Positive,
        NegativeDocumentation,
        NegativeCode,
        NegativeReport,
        NegativeOwnStateDifferentSituation,
        NegativeSubstring,
    };

    /// <summary>The three negative kinds the corpus must each hold at least once.</summary>
    public static readonly string[] RequiredNegatives = { NegativeDocumentation, NegativeCode, NegativeReport };

    /// <summary>What a kind expects: the positive kind an act, every other kind a decline.</summary>
    public static string ExpectationOf(string kind) =>
        string.Equals(kind, Positive, StringComparison.Ordinal) ? CaseExpectations.Act : CaseExpectations.Decline;
}

/// <summary>
/// THE CORPUS READER - the one place the corpus format is read, used by the harness and by the corpus
/// tests so the two cannot disagree about what a case is.
///
/// A screen is read as UTF-8 bytes exactly as captured and split on line breaks into the rows the engine
/// reads. It is never trimmed, tidied or re-encoded here: the engine trims what it trims, and a corpus
/// that was cleaned before it reached the engine would be testing a screen no session ever showed.
/// </summary>
public static class ScreenCorpus
{
    public const string RulesFileName = "rules.json";
    public const string CasesDirectoryName = "cases";
    public const string CaseFileName = "case.json";
    public const string ScreenFileName = "screen.txt";

    /// <summary>The stamp every corpus rule carries as created and updated. The corpus is fixtures, so
    /// the stamp is fixed rather than the clock.</summary>
    public static readonly DateTime RuleStampUtc = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions CaseJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Read <c>rules.json</c> into the rules the engine judges every case against: scope all
    /// sessions, dry run, nobody promoted them.</summary>
    /// <exception cref="FileNotFoundException">There is no rules file.</exception>
    /// <exception cref="InvalidDataException">The file is not an array of rules, or a rule has no guid id.</exception>
    public static IReadOnlyList<SessionRule> ReadRules(string corpusDirectory)
    {
        var path = Path.Combine(corpusDirectory, RulesFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("the corpus has no " + RulesFileName + " at " + path, path);

        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(RulesFileName + " must be one array of rules; it is " + document.RootElement.ValueKind);

        var rules = new List<SessionRule>();
        var position = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            position++;
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("rule " + position + " in " + RulesFileName + " is not an object");

            var idText = RuleCallJson.Text(element, "id") ?? "";
            if (!Guid.TryParse(idText, out var id))
                throw new InvalidDataException("rule " + position + " in " + RulesFileName + " has no guid id (got '" + idText + "')");

            var triggerWords = new List<string>();
            if (element.TryGetProperty("triggerWords", out var words) && words.ValueKind == JsonValueKind.Array)
                foreach (var word in words.EnumerateArray()) triggerWords.Add(RuleCallJson.Scalar(word));

            // The calls are read by the SAME reader the rule-writing route and the agent reply use, so a
            // check written in the corpus means what it would mean anywhere else in the feature.
            var calls = new List<RulePrimitiveCall>();
            if (element.TryGetProperty("calls", out var callsElement) && callsElement.ValueKind == JsonValueKind.Array)
                foreach (var call in callsElement.EnumerateArray()) calls.Add(RuleCallJson.ReadCall(call));

            rules.Add(new SessionRule(
                Id: id,
                Instruction: RuleCallJson.Text(element, "instruction") ?? "",
                ScreenDescription: RuleCallJson.Text(element, "screenDescription") ?? "",
                TriggerWords: triggerWords,
                Calls: calls,
                Scope: RuleScope.AllSessions,
                CooldownSeconds: Number(element, "cooldownSeconds"),
                DailyCap: Number(element, "dailyCap"),
                State: RuleState.DryRun,
                PromotedBy: "",
                CreatedUtc: RuleStampUtc,
                UpdatedUtc: RuleStampUtc));
        }
        return rules;
    }

    /// <summary>Every case under <c>cases/</c>, in directory-name order. A case directory without a
    /// <c>case.json</c> or a <c>screen.txt</c> is a corpus defect and throws by name.</summary>
    /// <exception cref="DirectoryNotFoundException">There is no cases directory.</exception>
    /// <exception cref="FileNotFoundException">A case directory lacks one of its two files.</exception>
    public static IReadOnlyList<ScreenCase> ReadCases(string corpusDirectory)
    {
        var casesDirectory = Path.Combine(corpusDirectory, CasesDirectoryName);
        if (!Directory.Exists(casesDirectory))
            throw new DirectoryNotFoundException("the corpus has no " + CasesDirectoryName + " directory at " + casesDirectory);

        return Directory.GetDirectories(casesDirectory)
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Select(ReadCase)
            .ToList();
    }

    /// <summary>One case from its directory.</summary>
    /// <exception cref="FileNotFoundException">The directory lacks one of its two files.</exception>
    /// <exception cref="InvalidDataException">The case file is empty.</exception>
    public static ScreenCase ReadCase(string caseDirectory)
    {
        var name = Path.GetFileName(caseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var casePath = Path.Combine(caseDirectory, CaseFileName);
        var screenPath = Path.Combine(caseDirectory, ScreenFileName);
        if (!File.Exists(casePath))
            throw new FileNotFoundException("case " + name + " has no " + CaseFileName, casePath);
        if (!File.Exists(screenPath))
            throw new FileNotFoundException("case " + name + " has no " + ScreenFileName, screenPath);

        var record = JsonSerializer.Deserialize<CaseRecord>(File.ReadAllText(casePath, Encoding.UTF8), CaseJson)
            ?? throw new InvalidDataException("case " + name + ": " + CaseFileName + " is empty");

        var bytes = File.ReadAllBytes(screenPath);
        return new ScreenCase(caseDirectory, record, SplitRows(bytes), bytes);
    }

    /// <summary>The screen bytes as the rows the engine reads: decoded as UTF-8 and split on any line
    /// break. Nothing is trimmed and nothing is dropped - a trailing line break yields a final empty row,
    /// exactly as a terminal grid carries a blank line.</summary>
    public static IReadOnlyList<string> SplitRows(byte[] screenBytes)
    {
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        return decoder.GetString(screenBytes).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
    }

    /// <summary>The screen as one piece of text, joined the way <c>RuleEvaluator.Join</c> joins it: rows
    /// trimmed at the end and separated by a newline. This is what the free checks read.</summary>
    public static string JoinAsTheEvaluatorDoes(IReadOnlyList<string> rows) =>
        string.Join("\n", rows.Select(r => r.TrimEnd()));

    /// <summary>The last <paramref name="tailLines"/> non-blank rows, trimmed at the end - the same tail
    /// rule <c>RuleAgentContract</c> applies before the screen goes into the question. Trigger words above
    /// this window are words the model never sees.</summary>
    public static IReadOnlyList<string> TailAsTheContractDoes(IReadOnlyList<string> rows, int tailLines)
    {
        if (rows.Count == 0 || tailLines <= 0) return Array.Empty<string>();
        var content = rows.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.TrimEnd()).ToList();
        if (content.Count <= tailLines) return content;
        return content.GetRange(content.Count - tailLines, tailLines);
    }

    /// <summary>Whether every byte is 7-bit ASCII.</summary>
    public static bool IsPureAscii(byte[] bytes) => bytes.All(b => b < 0x80);

    private static int Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) return parsed;
        return 0;
    }
}
