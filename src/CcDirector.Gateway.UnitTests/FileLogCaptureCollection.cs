using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// FileLog.RedirectForTests swaps one process-wide writer, so every test that owns that seam must run with
/// no other test class beside it. The Gateway unit assembly otherwise runs four classes in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileLogCaptureCollection
{
    public const string Name = "FileLog capture";
}
