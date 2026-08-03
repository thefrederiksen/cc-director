using CcDirector.Core.Utilities;

namespace CcDirector.Gateway;

/// <summary>
/// Which of the live Gateway processes is the one serving production, so that only ONE of them does
/// background work (issue #2398, stage 1).
///
/// WHY THIS EXISTS. A deploy runs TWO full Gateway containers at once and always has: the workflow warms
/// the staging slot, swaps it into production, then waits for roughly forty-five seconds of unbroken
/// health before stopping the old one
/// (<c>.github/workflows/deploy-hosted-gateway.yml:400</c> - that wait is deliberate, because stopping
/// staging earlier once took production down). Both processes run their cron sweep, their work-list
/// runner, their retention sweeps and their dictation delivery during that window, and every one of those
/// guards overlap with a PER-PROCESS lock, which two processes do not share. So a deploy can already run
/// a scheduled job twice, steal a claim, or inject a dictation twice. That is true today, before any of
/// the handover work in #2398.
///
/// THE CHEAP FIX IS NOT TO MAKE SIX SUBSYSTEMS CROSS-PROCESS SAFE. It is to make sure only one process is
/// ever ACTING. There is no concurrency to make safe if only the production instance does the work.
///
/// HOW A PROCESS KNOWS. It asks the public address who is answering there and compares the answer to
/// itself: <c>GET {CC_GATEWAY_PUBLIC_URL}/healthz</c> returns an <c>instance</c> that is unique per boot
/// (<see cref="GatewayInstanceIdentity"/>). If that instance is me, I am production. This needs no
/// coordination, no lease, no new credential and no change to the deploy workflow, and it resolves
/// correctly at every point of a swap:
///
///   - while staging warms, it asks and hears the OLD container: not me, so it stays passive. Correct -
///     a warming slot must not be running cron.
///   - the swap re-points the public address at the new container. It now hears ITSELF: it becomes
///     active.
///   - the old container asks and hears the NEW one: not me, so it goes passive and stops acting, while
///     still serving the connections it holds.
///
/// ASYMMETRIC ON PURPOSE. Going PASSIVE happens on a single reading, because the cost of being wrong is
/// duplicate work against a shared database. Becoming ACTIVE requires several consecutive agreeing
/// readings, because the front door flaps between old and new for a few seconds after a swap and a role
/// that oscillated with it would be worse than either state. Both directions therefore err towards
/// nobody acting rather than two.
///
/// WHAT HAPPENS IF THE CHECK CANNOT ANSWER. The process stays passive and says so LOUDLY on every failed
/// attempt. That is the safe direction - silence is better than duplication - but it is a real
/// degradation: a Gateway that can never reach its own public address will never run scheduled work. It
/// is deliberately noisy so that shows up as an operator problem rather than as jobs quietly not running.
///
/// SELF-HOST IS UNAFFECTED. A local install has one Gateway, no slots and no public URL, so it is
/// production by definition and never polls anything.
/// </summary>
public sealed class GatewayInstanceRole : IDisposable
{
    /// <summary>How often a hosted process re-checks who is serving production.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long a single check may take before it counts as unanswered.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Consecutive readings that must agree "production is me" before this process starts acting. More
    /// than one because the front door alternates between the old and the new instance for several
    /// seconds after a swap; a single agreeing reading during that flap is not evidence.
    /// </summary>
    private const int ReadingsToBecomeActive = 3;

    private readonly string _selfInstanceId;
    private readonly string? _publicBaseUrl;
    private readonly bool _hosted;
    private readonly Func<string, CancellationToken, Task<string?>> _readInstanceAt;
    private readonly Timer? _timer;

    private int _agreeingReadings;
    private volatile bool _isProduction;
    private bool _disposed;

    /// <summary>
    /// True when this process should do background work. Always true on self-host. On hosted it is false
    /// until several consecutive checks agree that the public address is answering with THIS instance.
    /// </summary>
    public bool IsProduction => _isProduction;

    /// <summary>The last reason the role is what it is, for the health surface and the log.</summary>
    public string Reason { get; private set; } = "starting";

    /// <param name="selfInstanceId">This process's per-boot identity, as reported on /healthz.</param>
    /// <param name="publicBaseUrl">The public address, or null/blank when there is none (self-host).</param>
    /// <param name="hosted">Whether this is the hosted Gateway. A self-host install is always production.</param>
    /// <param name="readInstanceAt">Reads the <c>instance</c> field from a Gateway's /healthz. A seam so the
    /// role logic can be tested without a server; production passes null for the real HTTP read.</param>
    public GatewayInstanceRole(
        string selfInstanceId,
        string? publicBaseUrl,
        bool hosted,
        Func<string, CancellationToken, Task<string?>>? readInstanceAt = null)
    {
        _selfInstanceId = selfInstanceId;
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl!.TrimEnd('/');
        _hosted = hosted;
        _readInstanceAt = readInstanceAt ?? ReadInstanceOverHttpAsync;

        if (!_hosted || _publicBaseUrl is null)
        {
            // One Gateway, no slots, nothing to be confused with.
            _isProduction = true;
            Reason = _hosted ? "hosted but no public URL configured - treating as production" : "self-host";
            FileLog.Write($"[GatewayInstanceRole] {Reason}; background work enabled");
            return;
        }

        FileLog.Write($"[GatewayInstanceRole] hosted: instance={_selfInstanceId}, public={_publicBaseUrl}; "
            + "starting PASSIVE until the public address is confirmed to be this instance");
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, CheckInterval);
    }

    /// <summary>
    /// One role check. Exposed for tests so the decision can be driven a reading at a time rather than
    /// waited on. Never throws: an unanswered check is a passive reading, not a crash.
    /// </summary>
    public async Task CheckAsync()
    {
        if (_disposed || _publicBaseUrl is null) return;
        string? serving;
        try
        {
            using var cts = new CancellationTokenSource(CheckTimeout);
            serving = await _readInstanceAt($"{_publicBaseUrl}/healthz", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            serving = null;
            FileLog.Write($"[GatewayInstanceRole] check FAILED ({ex.GetType().Name}); staying passive");
        }

        if (serving is not null && string.Equals(serving, _selfInstanceId, StringComparison.Ordinal))
        {
            _agreeingReadings++;
            if (!_isProduction && _agreeingReadings >= ReadingsToBecomeActive)
            {
                _isProduction = true;
                Reason = $"the public address answers with this instance ({_agreeingReadings} readings)";
                FileLog.Write($"[GatewayInstanceRole] now PRODUCTION: {Reason}; background work enabled");
            }
            return;
        }

        // Anything that is not a confirmed "it is me" retires the streak and stands this process down
        // IMMEDIATELY. One reading is enough in this direction: acting when another process is production
        // means duplicate scheduled work against one database, and standing down costs nothing but a delay.
        _agreeingReadings = 0;
        var why = serving is null
            ? "the public address did not answer"
            : $"the public address is answering with another instance ({serving})";
        if (_isProduction)
        {
            _isProduction = false;
            Reason = why;
            FileLog.Write($"[GatewayInstanceRole] now PASSIVE: {why}; background work stood down "
                + "(existing connections keep being served)");
        }
        else if (Reason != why)
        {
            Reason = why;
            FileLog.Write($"[GatewayInstanceRole] still passive: {why}");
        }
    }

    /// <summary>The real read: ask a Gateway's /healthz for its instance id. Null when it cannot be read.</summary>
    private static async Task<string?> ReadInstanceOverHttpAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = CheckTimeout };
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return GatewayInstanceIdentity.ReadInstanceFromHealthJson(body);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
    }
}
