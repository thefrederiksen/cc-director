using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Events;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The drift-alert channels (Network Diagnostics mission, P5). It delivers the monitor's Decide-machine
/// outputs - which the machine already emits correctly and false-alert-proof - to two channels:
///   - an ambient DOORBELL event (an in-app/Cockpit "your home network is slow" indicator), and
///   - an OWNER EMAIL with the specific fix, naming the drifted device.
///
/// Safety, because this is the one place a mistake would put a false "your home is slow" in front of the
/// owner: it adds NO detection of its own (the machine is the sole source of truth); it is 401-EXPLICIT
/// (when not signed in there is no account to email from, so the doorbell still fires but NO email is sent
/// and nothing is fabricated); it caps owner emails per day so a broken-for-hours network cannot spam; it
/// fires one drift email per EPISODE (the machine's rising edge is already one-shot); and it sends a
/// "recovered" email ONLY to a device it actually warned about. It never touches the needs-you badge.
/// Plain English, ASCII, names the device, no fabrication.
/// </summary>
public sealed class NetDiagAlertService
{
    public const int DefaultDailyEmailCap = 3;

    // A fixed ring key for these gateway-originated network events (they are not tied to a Director/session).
    private const string RingKey = "network";

    private readonly DirectorEventLog _events;
    private readonly Func<string, string, string, Task<bool>> _sendEmail; // (token, subject, bodyText) -> sent?
    private readonly Func<string?> _getToken;
    private readonly int _dailyEmailCap;
    private readonly Func<DateTime> _nowUtc;

    private readonly object _gate = new();
    private DateOnly _emailDay;
    private int _emailsToday;
    private readonly HashSet<string> _emailedEpisode = new(StringComparer.Ordinal);

    /// <param name="sendEmail">Sends an owner email (token, subject, bodyText) and returns whether it sent. Production closes over AccountNotifyClient.SendOwnerAsync; tests inject a fake.</param>
    public NetDiagAlertService(
        DirectorEventLog events, Func<string?> getToken, Func<string, string, string, Task<bool>> sendEmail,
        int? dailyEmailCap = null, Func<DateTime>? nowUtc = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _getToken = getToken ?? throw new ArgumentNullException(nameof(getToken));
        _sendEmail = sendEmail ?? throw new ArgumentNullException(nameof(sendEmail));
        _dailyEmailCap = dailyEmailCap ?? DefaultDailyEmailCap;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>Persistent home drift for <paramref name="deviceName"/>: doorbell always; owner email if signed in + under the daily cap.</summary>
    public void OnDrift(string deviceName)
    {
        try { _events.Record(RingKey, "", DoorbellEvents.NetworkDrift, deviceName); }
        catch (Exception ex) { FileLog.Write($"[NetDiagAlert] doorbell drift failed: {ex.Message}"); }

        // 401 FIRST, before touching the cap or the episode set. If not signed in we send only the doorbell
        // and must NOT burn the daily cap or mark the device "warned" - otherwise a later sign-in + resolve
        // would email a false "recovered" for a drift the owner never received (Architect review finding 1).
        var token = _getToken();
        if (string.IsNullOrEmpty(token))
        {
            FileLog.Write($"[NetDiagAlert] drift for {deviceName}: not signed in - doorbell only, no email");
            return;
        }

        bool underCap;
        lock (_gate)
        {
            RollDay();
            underCap = _emailsToday < _dailyEmailCap;
            if (underCap) _emailsToday++;
        }
        if (!underCap)
        {
            FileLog.Write($"[NetDiagAlert] drift for {deviceName}: daily email cap ({_dailyEmailCap}) reached - doorbell only");
            return;
        }

        // Mark the device "warned" only when the email actually SENDS (inside SendAsync), so
        // resolve-only-if-warned holds even if the cloud send fails.
        _ = SendAsync(token, DriftSubject(deviceName), DriftBodyText(deviceName), markWarnedDevice: deviceName);
    }

    /// <summary>Observed recovery for <paramref name="deviceName"/>: doorbell always; a "recovered" email ONLY if we emailed a drift for it.</summary>
    public void OnResolve(string deviceName)
    {
        try { _events.Record(RingKey, "", DoorbellEvents.NetworkRecovered, deviceName); }
        catch (Exception ex) { FileLog.Write($"[NetDiagAlert] doorbell recovered failed: {ex.Message}"); }

        bool weWarned;
        lock (_gate) { weWarned = _emailedEpisode.Remove(deviceName); }
        if (!weWarned) return; // never send an unsolicited "recovered" - only close the loop we opened

        var token = _getToken();
        if (string.IsNullOrEmpty(token)) return;

        _ = SendAsync(token, RecoverySubject(deviceName), RecoveryBodyText(deviceName), markWarnedDevice: null);
    }

    // Sends the owner email; on success, marks markWarnedDevice as "warned" so a later resolve emails a
    // recovery only for a drift the owner actually received (null for the recovery email itself).
    private async Task SendAsync(string token, string subject, string bodyText, string? markWarnedDevice)
    {
        try
        {
            var sent = await _sendEmail(token, subject, bodyText).ConfigureAwait(false);
            if (sent)
            {
                if (markWarnedDevice is not null)
                    lock (_gate) { _emailedEpisode.Add(markWarnedDevice); }
                FileLog.Write("[NetDiagAlert] owner email sent to the account owner.");
            }
            else
            {
                FileLog.Write("[NetDiagAlert] owner email was not sent (cloud declined).");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NetDiagAlert] owner email failed: {ex.Message}");
        }
    }

    private void RollDay()
    {
        var today = DateOnly.FromDateTime(_nowUtc());
        if (today != _emailDay) { _emailDay = today; _emailsToday = 0; }
    }

    // ----- pure message content (unit-tested) -----

    public static string DriftSubject(string deviceName) => $"DevThrottle: your home network is slow ({deviceName})";

    public static string DriftBodyText(string deviceName) =>
        $"{deviceName} has been relaying through a distant server instead of a direct connection on your home network for several minutes.\r\n\r\n" +
        "This only fires when it persists - a brief slow start when you open the app is normal and is not reported.\r\n\r\n" +
        "To get back to a fast, direct connection:\r\n" +
        $"- Keep the DevThrottle app open on {deviceName}.\r\n" +
        "- In the Tailscale app on that device, make sure it has local-network access.\r\n" +
        "- Make sure no Tailscale exit node is routing your home traffic.\r\n" +
        "- On your router, enable UPnP and do not isolate wireless clients, so a direct path can form.\r\n\r\n" +
        "You will get a follow-up when it is back on a direct path.";

    public static string RecoverySubject(string deviceName) => $"DevThrottle: home network recovered ({deviceName})";

    public static string RecoveryBodyText(string deviceName) =>
        $"{deviceName} is back on a direct connection on your home network. No action needed.";
}
