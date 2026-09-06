using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The administrator account lookup:
///
///   GET /gateway/admin/accounts?email=someone@example.com   ->  { account, email, computers: [...] }
///   GET /gateway/admin/accounts?subject=&lt;account subject&gt;
///
/// WHY IT EXISTS. Every administrator surface we have speaks EMAILS and website member ids, and every
/// Gateway capability that acts on an account - the turn-log capture switch is the first, and it will not
/// be the last - is keyed on the Gateway's own account identifier or on a Director id. Nothing bridged the
/// two. The result was an administrator screen with an "Account" box that could not be filled in for
/// anybody except the administrator's own fleet, because the only way to learn an account identifier was to
/// already be inside that account. A capability nobody can address is not a capability.
///
/// It also answers the question that comes immediately after "who is this?": WHICH COMPUTERS. An account is
/// not a useful unit for capture - a person has machines, and a decision to record one machine is very
/// different from a decision to record all of them. So the lookup returns the Directors the Gateway
/// currently knows for that account, each with the machine name a human recognises and the identifier the
/// switch actually takes.
///
/// IT IS A LOOKUP, NOT A DIRECTORY. It answers about ONE account named by the caller. There is deliberately
/// no "list every account" leg: an administrator surface that enumerates the customer base invites being
/// used as one, and every legitimate use here starts from a person somebody has already identified on the
/// website.
///
/// AUTHORIZATION is the same administrator service token the trial and turn-log surfaces carry, called
/// rather than copied so there is one definition of who may act as an administrator here. See
/// <see cref="AdminTurnLogEndpoint"/> for why one secret guards one screen.
///
/// IT RETURNS NO TERMINAL CONTENT AND NO SESSION CONTENT - an account identifier, an email the Gateway
/// already recorded at mint time, and the machines. Nothing here reads anybody's work.
/// </summary>
internal static class AdminAccountLookupEndpoint
{
    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; the endpoint carries its own gate.</summary>
    public const string Path = "/gateway/admin/accounts";

    public static void Map(
        IEndpointRouteBuilder app,
        TenantRegistry tenants,
        DirectorRegistry directors)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(directors);

        app.MapGet(Path, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

                var email = ctx.Request.Query["email"].ToString().Trim();
                var subject = ctx.Request.Query["subject"].ToString().Trim();
                return Handle(tenants, directors, email, subject);
            }
            catch (Exception ex)
            {
                // UNKNOWN, not "no such account". Telling an administrator an account does not exist
                // because a query threw would have them conclude somebody was never a customer.
                FileLog.Write($"[AdminAccountLookupEndpoint] GET {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the lookup could not be completed" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[AdminAccountLookupEndpoint] mapped {Path} (service-token authorized)");
    }

    /// <summary>Internal so every branch is testable without standing a host up.</summary>
    internal static IResult Handle(
        TenantRegistry tenants,
        DirectorRegistry directors,
        string? email,
        string? subject)
    {
        var hasEmail = !string.IsNullOrWhiteSpace(email);
        var hasSubject = !string.IsNullOrWhiteSpace(subject);

        // EXACTLY ONE. Accepting both and preferring one silently would let a caller that resolved an email
        // to the wrong subject act on an account it never named, and the reply would look correct.
        if (hasEmail == hasSubject)
        {
            return Results.BadRequest(new
            {
                error = "name exactly one account: either ?email= or ?subject=",
            });
        }

        TenantId? tenant = null;
        if (hasSubject)
        {
            tenant = tenants.LookupBySubject(subject!.Trim());
        }
        else
        {
            // The Gateway records the account email when it mints a tenant, so this resolves without the
            // website. Compared case-insensitively because an address is not case-sensitive to a human and
            // an administrator will type it as they read it.
            var match = tenants.ListAll()
                .Where(t => !string.IsNullOrWhiteSpace(t.Email))
                .Where(t => string.Equals(t.Email, email!.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            // AMBIGUITY IS REFUSED, NOT GUESSED. Two tenants recorded against one address is a state we
            // should not paper over by picking the first - acting on the wrong one would switch capture on
            // for an account nobody named.
            if (match.Count > 1)
            {
                // The COUNT is deliberately not returned. It answered a question nobody needs and told a
                // caller how many accounts share an address.
                return Results.Json(new
                {
                    error = "more than one account is recorded against that email; name the account by ?subject= instead",
                }, statusCode: StatusCodes.Status409Conflict);
            }
            if (match.Count == 1) tenant = new TenantId(match[0].TenantId);
        }

        if (tenant is not { IsValid: true } found)
        {
            // A plain, honest negative. It says what was searched so an administrator can tell "this person
            // has never run a Director" from "I typed the address wrong".
            return Results.Json(new
            {
                found = false,
                searched_by = hasSubject ? "subject" : "email",
                message = "no account on this Gateway matches that. An account appears here once it has run "
                        + "something - a member who has only ever signed in to the website has no account here yet.",
            }, statusCode: StatusCodes.Status404NotFound);
        }

        // The machines, read through the registry's PER-TENANT listing rather than the fleet-wide one.
        // That is deliberate and it is the narrower capability: the tenant has already been resolved from
        // the account the caller named, and the registry then filters on its own key. The fleet-wide
        // accessor exists and takes a SystemScope token, and this route does not hold one - so it cannot
        // read across accounts even by mistake, and adding a general cross-tenant reader is not something
        // an account lookup should quietly do on the way past.
        var computers = directors.ListDirectors(found)
            .OrderBy(d => d.MachineName ?? "", StringComparer.OrdinalIgnoreCase)
            // MINIMISED. The operating-system username was here and is gone: nothing an administrator does
            // with this needs it, and a lookup that hands back more than the job requires turns a shared
            // credential into a better reconnaissance tool than it has to be. What is left is what the
            // capture switch actually takes plus the name a human recognises.
            .Select(d => new
            {
                director_id = d.DirectorId,
                machine_name = d.MachineName,
                last_seen_utc = d.LastSeen,
            })
            .ToList();

        // EVERY LOOKUP IS LOGGED. This route exists to name other people's accounts, so the fact that it
        // was used is itself worth recording - a shared credential with no trail behind it cannot answer
        // "who went looking, and when". The tenant is written in its log-safe form, never raw.
        FileLog.Write($"[AdminAccountLookupEndpoint] account lookup by {(hasSubject ? "subject" : "email")} -> {found.ToLogString()}");

        return Results.Json(new
        {
            found = true,
            account = found.Value,
            computers,
            // Said out loud because an empty list has two meanings and only one of them is a problem: an
            // account that has never connected a computer, and an account whose computers are all currently
            // away. This route only sees the ones the Gateway is holding right now.
            computers_note = computers.Count == 0
                ? "no computer for this account is currently connected to the Gateway, so none can be listed. "
                + "That is not the same as the account having none."
                : null,
        });
    }
}
