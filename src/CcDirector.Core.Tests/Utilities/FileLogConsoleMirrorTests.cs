using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

// =====================================================================================
// FileLog's hosted seams (issue #2203). A hosted deploy runs TWO Gateway containers at
// once against one storage mount, and the startup record of the container that fails is
// exactly what we need. Two guarantees are pinned here:
//   1. MirrorToConsole puts every line on standard output as well - the sink the
//      container platform captures per container, and the one with no queue in front of
//      it, so it survives the file-share stall that erased three startup records.
//   2. UseUniqueInstanceId REFUSES to run after Start(), instead of quietly leaving the
//      process writing to the shared file it was supposed to move off.
// =====================================================================================
public sealed class FileLogConsoleMirrorTests
{
    [Fact]
    public void Write_MirrorToConsoleOn_AlsoWritesTheLineToStandardOutput()
    {
        var originalOut = Console.Out;
        var captured = new StringWriter();
        var wasMirroring = FileLog.MirrorToConsole;

        using var scope = FileLog.RedirectForTests();
        try
        {
            Console.SetOut(captured);
            FileLog.MirrorToConsole = true;

            FileLog.Write("[Program] CC Director Gateway starting");

            Assert.Contains("[Program] CC Director Gateway starting", captured.ToString());
        }
        finally
        {
            FileLog.MirrorToConsole = wasMirroring;
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Write_MirrorToConsoleOff_KeepsStandardOutputClean()
    {
        // The mirror is switched off once the Gateway binds, because copying the running log into the
        // platform's log mount is a different outage. Off must mean silent.
        var originalOut = Console.Out;
        var captured = new StringWriter();
        var wasMirroring = FileLog.MirrorToConsole;

        using var scope = FileLog.RedirectForTests();
        try
        {
            Console.SetOut(captured);
            FileLog.MirrorToConsole = false;

            FileLog.Write("[GatewayHost] a routine line long after startup");

            Assert.DoesNotContain("a routine line long after startup", captured.ToString());
        }
        finally
        {
            FileLog.MirrorToConsole = wasMirroring;
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void UseUniqueInstanceId_AfterStart_ThrowsRatherThanSplittingTheRecord()
    {
        // Inside the scope the writer is running. Moving the file now would split this process's record
        // across two files, so this must fail loudly rather than half-apply.
        using var scope = FileLog.RedirectForTests();

        var ex = Assert.Throws<InvalidOperationException>(FileLog.UseUniqueInstanceId);
        Assert.Contains("before FileLog.Start()", ex.Message);
    }
}
