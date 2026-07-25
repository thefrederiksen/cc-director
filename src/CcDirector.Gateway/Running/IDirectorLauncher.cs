using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Asks a machine's launcher to start a Director (epic #479, #503), behind an interface so the cron
/// target resolver is unit-testable. Production is <see cref="RelayDirectorLauncher"/>, which posts
/// to the Gateway's own launcher relay (<c>POST /machines/{machine}/director/start</c>, #331) over
/// loopback - reusing the shipped launcher path rather than re-implementing launcher discovery.
/// </summary>
public interface IDirectorLauncher
{
    /// <summary>Request a Director start on <paramref name="machine"/>. Returns true if the launcher
    /// accepted the request (the Director then registers asynchronously), false otherwise.</summary>
    Task<bool> StartAsync(string machine, CancellationToken ct);
}

/// <summary>Production launcher: posts the shipped relay on the local Gateway (#331).</summary>
public sealed class RelayDirectorLauncher : IDirectorLauncher
{
    private readonly Func<int> _gatewayPort;
    private readonly string _token;

    /// <param name="gatewayPort">
    /// Read LATE, never captured (issue #2161). A Gateway started on an operating-system-assigned port does
    /// not know its own number until the listener binds, which happens after this launcher is constructed -
    /// so an int here would freeze the placeholder 0 and every relay would dial a port nothing serves.
    /// </param>
    public RelayDirectorLauncher(Func<int> gatewayPort, string token)
    {
        _gatewayPort = gatewayPort ?? throw new ArgumentNullException(nameof(gatewayPort));
        _token = token ?? "";
    }

    public async Task<bool> StartAsync(string machine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machine))
            return false;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gatewayPort()}/"), Timeout = TimeSpan.FromSeconds(15) };
            if (!string.IsNullOrEmpty(_token))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            var resp = await http.PostAsync($"machines/{Uri.EscapeDataString(machine)}/director/start", content: null, ct);
            FileLog.Write($"[RelayDirectorLauncher] start machine={machine} -> {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RelayDirectorLauncher] start machine={machine} FAILED: {ex.Message}");
            return false;
        }
    }
}
