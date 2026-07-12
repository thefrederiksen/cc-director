using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class DirectorRestartConfigTests
{
    private static DateTime At(int hour) => new(2026, 7, 11, hour, 30, 0);

    [Fact]
    public void BlockReason_Disabled_Blocks()
    {
        var cfg = new DirectorRestartConfig(Enabled: false, WindowStartHour: 2, WindowEndHour: 5);
        Assert.Contains("disabled", cfg.BlockReason(At(3), busySessions: 0)!);
    }

    [Fact]
    public void BlockReason_UnknownBusyCount_Blocks()
    {
        // An older Director that does not report busySessions must never be restarted on
        // missing evidence.
        var cfg = new DirectorRestartConfig(Enabled: true, WindowStartHour: 2, WindowEndHour: 5);
        Assert.Contains("missing evidence", cfg.BlockReason(At(3), busySessions: null)!);
    }

    [Fact]
    public void BlockReason_BusySessions_Blocks_EvenInsideWindow()
    {
        var cfg = new DirectorRestartConfig(Enabled: true, WindowStartHour: 2, WindowEndHour: 5);
        Assert.Contains("actively working", cfg.BlockReason(At(3), busySessions: 2)!);
    }

    [Fact]
    public void BlockReason_OutsideWindow_Blocks_EvenWhenIdle()
    {
        var cfg = new DirectorRestartConfig(Enabled: true, WindowStartHour: 2, WindowEndHour: 5);
        Assert.Contains("maintenance window", cfg.BlockReason(At(14), busySessions: 0)!);
    }

    [Fact]
    public void BlockReason_IdleInsideWindow_Allows()
    {
        var cfg = new DirectorRestartConfig(Enabled: true, WindowStartHour: 2, WindowEndHour: 5);
        Assert.Null(cfg.BlockReason(At(3), busySessions: 0));
    }

    [Fact]
    public void InWindow_SpanningMidnight_CoversBothSides()
    {
        var cfg = new DirectorRestartConfig(Enabled: true, WindowStartHour: 22, WindowEndHour: 6);
        Assert.True(cfg.InWindow(23));
        Assert.True(cfg.InWindow(2));
        Assert.False(cfg.InWindow(12));
    }

    [Fact]
    public void InWindow_StartEqualsEnd_MeansAlways()
    {
        var cfg = new DirectorRestartConfig(Enabled: true, WindowStartHour: 4, WindowEndHour: 4);
        Assert.True(cfg.InWindow(4));
        Assert.True(cfg.InWindow(15));
    }
}
