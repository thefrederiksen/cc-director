using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

public class AnsiParserSyncBlockTrace
{
    private readonly ITestOutputHelper _output;

    public AnsiParserSyncBlockTrace(ITestOutputHelper output) => _output = output;

    private static byte[] LoadCapture(string filename)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");
        var path = Path.Combine(dir, filename);
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var projectDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "TestData");
        path = Path.Combine(projectDir, filename);
        if (File.Exists(path)) return File.ReadAllBytes(path);
        throw new FileNotFoundException($"Capture file not found: {filename}");
    }

    [Fact]
    public void FindLastSyncBlockThatTouchesRow17()
    {
        var rawBytes = LoadCapture("claude-stray-chars.bin");

        // Find all ESC[?2026h (start sync) and ESC[?2026l (end sync) positions
        // ESC[?2026h = 1B 5B 3F 32 30 32 36 68
        // ESC[?2026l = 1B 5B 3F 32 30 32 36 6C
        var syncStarts = new List<int>();
        var syncEnds = new List<int>();

        var startPattern = Encoding.ASCII.GetBytes("\x1b[?2026h");
        var endPattern = Encoding.ASCII.GetBytes("\x1b[?2026l");

        for (int i = 0; i < rawBytes.Length - 8; i++)
        {
            if (MatchesAt(rawBytes, i, startPattern))
                syncStarts.Add(i);
            if (MatchesAt(rawBytes, i, endPattern))
                syncEnds.Add(i);
        }

        _output.WriteLine($"Found {syncStarts.Count} sync starts, {syncEnds.Count} sync ends");

        // Now find which sync blocks contain cursor positioning to row 17 or 18
        // ESC[17;...H or ESC[18;...H
        // We're looking for the LAST one that touches row 17/18
        int lastBlockStart = -1;
        int lastBlockEnd = -1;

        for (int i = syncStarts.Count - 1; i >= 0; i--)
        {
            int start = syncStarts[i];
            // Find matching end
            int end = -1;
            foreach (var e in syncEnds)
            {
                if (e > start) { end = e; break; }
            }
            if (end == -1) continue;

            // Check if this block contains a cursor move to row 17 or 18
            var blockBytes = new byte[end - start];
            Array.Copy(rawBytes, start, blockBytes, 0, blockBytes.Length);
            var blockText = Encoding.ASCII.GetString(blockBytes);

            // Look for ESC[17; or ESC[18; (cursor positioning to those rows)
            if (blockText.Contains("\x1b[17;") || blockText.Contains("\x1b[18;"))
            {
                lastBlockStart = start;
                lastBlockEnd = end;
                break;
            }
        }

        if (lastBlockStart == -1)
        {
            _output.WriteLine("No sync block touches row 17/18");

            // Maybe ink uses relative cursor movement instead
            // Let's just dump the last 2000 bytes as text to see what's there
            int dumpStart = Math.Max(0, rawBytes.Length - 2000);
            var tail = new byte[rawBytes.Length - dumpStart];
            Array.Copy(rawBytes, dumpStart, tail, 0, tail.Length);
            var text = Encoding.UTF8.GetString(tail)
                .Replace("\x1b", "ESC")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
            _output.WriteLine($"\nLast 2000 bytes as text:\n{text}");
            return;
        }

        _output.WriteLine($"Last sync block touching row 17/18: bytes {lastBlockStart}-{lastBlockEnd} ({lastBlockEnd - lastBlockStart} bytes)");

        // Dump the sync block content
        var block = new byte[lastBlockEnd - lastBlockStart + 8];
        Array.Copy(rawBytes, lastBlockStart, block, 0, block.Length);
        var blockContent = Encoding.UTF8.GetString(block)
            .Replace("\x1b", "ESC")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");

        // Show first 3000 chars (blocks can be large)
        if (blockContent.Length > 3000)
            blockContent = blockContent[..3000] + "... (truncated)";

        _output.WriteLine($"\nBlock content:\n{blockContent}");
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        if (offset + pattern.Length > data.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (data[offset + i] != pattern[i]) return false;
        return true;
    }
}
