using CcDirector.Gateway.Api;
using CcDirector.Gateway.Events;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The P5 drift-alert delivery (Network Diagnostics mission). The detector guarantees no false alert; these
/// tests guard the DELIVERY safety the Architect flagged: the 401 (not-signed-in) path sends no email, the
/// daily cap prevents spam and resets per day, a "recovered" email only goes to a device we actually warned
/// about, and the message names the device with the specific fix. The email send is injected so no real
/// send happens; the fake completes synchronously so the fire-and-forget is deterministic.
/// </summary>
public sealed class NetDiagAlertServiceTests
{
    private readonly List<(string token, string subject, string body)> _sent = new();

    private NetDiagAlertService Make(string? token = "tok", int cap = 3, Func<DateTime>? now = null) =>
        new(
            new DirectorEventLog(),
            () => token,
            (t, s, b) => { _sent.Add((t, s, b)); return Task.FromResult(true); },
            dailyEmailCap: cap,
            nowUtc: now);

    // ----- message content -----

    [Fact]
    public void DriftMessage_NamesDeviceAndCarriesTheFixSteps()
    {
        Assert.Contains("Phone", NetDiagAlertService.DriftSubject("Phone"));
        var body = NetDiagAlertService.DriftBodyText("Phone");
        Assert.Contains("Phone", body);
        Assert.Contains("local-network access", body);
        Assert.Contains("exit node", body);
        Assert.Contains("UPnP", body);
    }

    [Fact]
    public void RecoveryMessage_NamesDevice()
    {
        Assert.Contains("Phone", NetDiagAlertService.RecoverySubject("Phone"));
        Assert.Contains("Phone", NetDiagAlertService.RecoveryBodyText("Phone"));
    }

    // ----- delivery safety -----

    [Fact]
    public void OnDrift_SignedInUnderCap_SendsOneEmail()
    {
        Make().OnDrift("Phone");
        var one = Assert.Single(_sent);
        Assert.Contains("Phone", one.subject);
    }

    [Fact]
    public void OnDrift_NotSignedIn_SendsNoEmail()
    {
        Make(token: null).OnDrift("Phone");
        Assert.Empty(_sent); // 401-explicit: doorbell only, never a fabricated send
    }

    // Architect finding 1 regression: a not-signed-in drift must not burn the cap OR mark the device warned,
    // or a later sign-in + resolve would email a false "recovered" for a drift the owner never received.
    [Fact]
    public void OnDrift_NotSignedIn_BurnsNoCapAndMarksNoEpisode()
    {
        string? token = null;
        var s = new NetDiagAlertService(
            new DirectorEventLog(), () => token,
            (t, su, b) => { _sent.Add((t, su, b)); return Task.FromResult(true); },
            dailyEmailCap: 1);

        s.OnDrift("Phone"); // not signed in -> doorbell only
        Assert.Empty(_sent);

        // (a) episode NOT marked: sign in, resolve -> NO false 'recovered'
        token = "tok";
        s.OnResolve("Phone");
        Assert.Empty(_sent);

        // (b) cap NOT burned: a real signed-in drift still sends under the cap of 1
        s.OnDrift("Laptop");
        var one = Assert.Single(_sent);
        Assert.Contains("Laptop", one.subject);
    }

    [Fact]
    public void DailyCap_LimitsEmailsPerDay()
    {
        var s = Make(cap: 2);
        s.OnDrift("A");
        s.OnDrift("B");
        s.OnDrift("C"); // over the cap
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void DailyCap_ResetsTheNextDay()
    {
        var now = new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);
        var s = new NetDiagAlertService(
            new DirectorEventLog(), () => "tok",
            (t, su, b) => { _sent.Add((t, su, b)); return Task.FromResult(true); },
            dailyEmailCap: 1, nowUtc: () => now);

        s.OnDrift("A"); // day 1: sends
        s.OnDrift("B"); // day 1: cap hit, no send
        Assert.Single(_sent);

        now = now.AddDays(1);
        s.OnDrift("C"); // day 2: reset, sends
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void OnResolve_OnlyEmailsADeviceWeWarned()
    {
        var s = Make();

        s.OnResolve("Phone"); // never warned -> no unsolicited "recovered"
        Assert.Empty(_sent);

        s.OnDrift("Phone"); // 1 drift email
        s.OnResolve("Phone"); // recovery email, because we warned
        Assert.Equal(2, _sent.Count);
        Assert.Contains("recovered", _sent[1].subject);

        s.OnResolve("Phone"); // episode already closed -> no second recovery email
        Assert.Equal(2, _sent.Count);
    }
}
