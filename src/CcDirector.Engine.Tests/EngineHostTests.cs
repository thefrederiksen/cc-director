using CcDirector.Engine.Events;
using Xunit;

namespace CcDirector.Engine.Tests;

public sealed class EngineHostTests : IDisposable
{
    private readonly string _dbPath;

    public EngineHostTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_host_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Start_SetsIsRunning()
    {
        using var host = new EngineHost(new EngineOptions
        {
            DatabasePath = _dbPath,
            CheckIntervalSeconds = 3600
        });

        host.Start();

        Assert.True(host.IsRunning);
        await host.StopAsync();
    }

    [Fact]
    public async Task StopAsync_SetsIsRunningFalse()
    {
        using var host = new EngineHost(new EngineOptions
        {
            DatabasePath = _dbPath,
            CheckIntervalSeconds = 3600
        });

        host.Start();
        await host.StopAsync();

        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task Start_RaisesEngineStartedEvent()
    {
        var events = new List<EngineEvent>();
        using var host = new EngineHost(new EngineOptions
        {
            DatabasePath = _dbPath,
            CheckIntervalSeconds = 3600
        });
        host.OnEvent += e => events.Add(e);

        host.Start();
        await host.StopAsync();

        Assert.Contains(events, e => e.Type == EngineEventType.EngineStarted);
        Assert.Contains(events, e => e.Type == EngineEventType.EngineStopping);
        Assert.Contains(events, e => e.Type == EngineEventType.EngineStopped);
    }

    [Fact]
    public async Task GetStatus_ReturnsValidStatus()
    {
        using var host = new EngineHost(new EngineOptions
        {
            DatabasePath = _dbPath,
            CheckIntervalSeconds = 3600
        });

        host.Start();
        var status = host.GetStatus();
        await host.StopAsync();

        Assert.True(status.IsRunning);
        Assert.Equal(0, status.TotalJobs);
        Assert.Equal(0, status.EnabledJobs);
        Assert.Equal(0, status.RunningJobs);
    }

    [Fact]
    public void GetStatus_BeforeStart_ReturnsNotRunning()
    {
        using var host = new EngineHost(new EngineOptions
        {
            DatabasePath = _dbPath,
            CheckIntervalSeconds = 3600
        });

        var status = host.GetStatus();

        Assert.False(status.IsRunning);
    }

    [Fact]
    public async Task Database_ExposedAfterStart()
    {
        using var host = new EngineHost(new EngineOptions
        {
            DatabasePath = _dbPath,
            CheckIntervalSeconds = 3600
        });

        Assert.Null(host.Database);

        host.Start();
        Assert.NotNull(host.Database);
        await host.StopAsync();
    }
}
