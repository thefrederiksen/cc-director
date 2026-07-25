using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Tests.Drivers;

internal sealed class EmptyTranscriptReader : ITranscriptReader
{
    public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath) => new();

    public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath) => null;

    public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath) => new();

    /// <summary>Never compacted. Tests that care about compaction use <see cref="StubTranscriptReader"/>.</summary>
    public DateTime? LastCompactionUtc(string claudeSessionId, string repoPath) => null;
}

/// <summary>
/// A transcript store whose compaction mark the test sets - including "the mark appears only after N
/// reads", which is what a compaction that takes a while actually looks like from outside.
/// </summary>
internal sealed class StubTranscriptReader : ITranscriptReader
{
    private readonly int _readsBeforeMark;

    public StubTranscriptReader(DateTime? compactionUtc = null, int readsBeforeMark = 0)
    {
        CompactionUtc = compactionUtc;
        _readsBeforeMark = readsBeforeMark;
    }

    /// <summary>The mark the store reports once <see cref="Reads"/> passes readsBeforeMark.</summary>
    public DateTime? CompactionUtc { get; set; }

    /// <summary>How many times the mark has been read - the proof that waiting actually polls.</summary>
    public int Reads { get; private set; }

    public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath) => new();

    public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath) => null;

    public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath) => new();

    public DateTime? LastCompactionUtc(string claudeSessionId, string repoPath)
    {
        Reads++;
        return Reads > _readsBeforeMark ? CompactionUtc : null;
    }
}
