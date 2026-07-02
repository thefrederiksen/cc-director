using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The account credit-balance proxy (issue #884): <c>GET /account/credits</c>. The Settings account
/// section shows the signed-in account's balance and refreshes it after a hosted AI action, but the
/// Cockpit must NEVER hold the account token or call the cloud directly - the token lives on the
/// Gateway. So the Gateway proxies: it reads its own stored account token
/// (<see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/>, the SAME egress credential it
/// already uses for telemetry/devices), calls the cloud credits endpoint
/// (<see cref="AccountCreditsClient"/>, JWT-authed), and returns a local, token-free DTO.
///
/// Behaviour at the edges (no fabricated data):
/// <list type="bullet">
/// <item>Signed out / no credential -> an explicit <c>signedIn:false</c> envelope, never a fabricated
/// zero balance.</item>
/// <item>Cloud unreachable / erroring -> a clear 502 error (logged), never a fabricated balance.</item>
/// </list>
///
/// Security (DT-05): the raw account token NEVER appears in the response (no token field) and is never
/// logged. Inherits the host-wide Gateway token middleware like the other <c>/account</c> endpoints.
/// </summary>
internal static class AccountCreditsEndpoint
{
    /// <summary>Maps <c>GET /account/credits</c>.</summary>
    /// <param name="app">The route builder.</param>
    /// <param name="account">The Gateway-hosted credential service. Null on a host with no credential service.</param>
    /// <param name="credits">The cloud credits client (the injectable cloud egress seam).</param>
    public static void Map(IEndpointRouteBuilder app, DevThrottleAccountService? account, AccountCreditsClient credits)
    {
        if (credits is null) throw new ArgumentNullException(nameof(credits));

        app.MapGet("/account/credits", async (HttpContext ctx) =>
        {
            // Entry point: the delegate is the boundary, so the only try-catch lives here. A signed-out
            // Gateway is an expected state answered explicitly; a cloud failure is caught here and
            // reported as a clear error (never a fabricated balance).
            var token = account?.GetAccessTokenForForwarding();
            if (string.IsNullOrEmpty(token))
            {
                FileLog.Write("[AccountCreditsEndpoint] GET /account/credits: no account credential -> signedIn=false");
                return Results.Json(new AccountCreditsDto { SignedIn = false });
            }

            try
            {
                var result = await credits.GetCreditsAsync(token, ctx.RequestAborted).ConfigureAwait(false);

                // The most recent debit's magnitude is the last hosted action's cost (a debit's
                // amount is negative on the ledger); a top-up (credit) is not an action cost.
                long? lastDebit = null;
                foreach (var tx in result.Recent)
                {
                    if (tx.AmountMicros < 0)
                    {
                        lastDebit = -tx.AmountMicros;
                        break;
                    }
                }

                FileLog.Write($"[AccountCreditsEndpoint] GET /account/credits: signedIn=true, balanceMicros={result.BalanceMicros}");
                return Results.Json(new AccountCreditsDto
                {
                    SignedIn = true,
                    BalanceMicros = result.BalanceMicros,
                    LastDebitMicros = lastDebit,
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AccountCreditsEndpoint] GET /account/credits FAILED: {ex.Message}");
                return Results.Json(
                    new { error = "Could not reach the DevThrottle account service to read your balance. Try again shortly." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
