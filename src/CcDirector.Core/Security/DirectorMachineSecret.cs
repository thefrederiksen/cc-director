using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Security;

/// <summary>
/// The ONE secret a machine's Director accepts, and the only place that decides which one it is.
///
/// Two sources, in this order:
///   1. the SHARED fleet token (<c>gateway.token</c> in config.json) when this machine is attached
///      to a Gateway - the Gateway presents it on every proxied call, so the Director must accept it;
///   2. otherwise this machine's own persisted token,
///      <c>config/director/gateway-token.txt</c>, generated on first use.
///
/// This lives in Core rather than beside the middleware because everything that has to PRESENT the
/// credential is outside the web layer: the launcher stopping the Director, the desktop's own
/// self-probe, the command line. Each of those previously spelled the path out for itself and read
/// only the token FILE, which is correct on a standalone machine and silently wrong on a
/// Gateway-attached one - a mismatch that could not be noticed while the Control API accepted
/// everybody.
/// </summary>
public static class DirectorMachineSecret
{
    /// <summary>
    /// This machine's own persisted secret. Computed fresh rather than cached in a static, so it
    /// honours the current storage root - a test that redirects the root must not be answered with
    /// the real machine's path.
    /// </summary>
    public static string TokenFile => Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");

    /// <summary>
    /// The secret to accept, given whatever fleet token this Director is configured with. Pure, so
    /// the decision can be tested without a filesystem when the fleet token is present.
    /// </summary>
    public static string Resolve(string? fleetToken)
        => string.IsNullOrWhiteSpace(fleetToken) ? LoadOrCreate() : fleetToken.Trim();

    /// <summary>Read this machine's secret from disk; generate and persist one if absent.</summary>
    public static string LoadOrCreate()
    {
        var path = TokenFile;
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    FileLog.Write($"[DirectorMachineSecret] Loaded token from {path}");
                    return existing;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var token = Generate();
            File.WriteAllText(path, token);
            FileLog.Write($"[DirectorMachineSecret] Generated new token at {path}");
            return token;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorMachineSecret] LoadOrCreate FAILED: {ex.Message}");
            throw;
        }
    }

    private static string Generate()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
