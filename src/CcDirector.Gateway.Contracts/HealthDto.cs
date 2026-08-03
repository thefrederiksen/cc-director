namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /healthz response.
/// </summary>
public sealed class HealthDto
{
    public string Status { get; set; } = "ok";

    /// <summary>
    /// Fleet counts, or NULL when the responder has no honest number to give - in which case they are
    /// OMITTED from the JSON entirely rather than serialized as 0.
    ///
    /// Hosted Multi-Tenancy: /healthz is the PUBLIC unauthenticated liveness probe, so on the hosted
    /// Gateway it carries no credential and therefore has no tenant - and a fleet-GLOBAL count across every
    /// account is a cross-tenant disclosure. The aggregate is not computed there at all. These were plain
    /// <c>int</c> before, so "not computed" serialized as <c>0</c>: not a leak (0 is not the real count),
    /// but a FALSE statement rather than an absent one. /healthz is what every Director and endpoint
    /// selector dials, so a permanent 0 reads as a dead fleet. Absent is honest; zero is misleading.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Directors { get; set; }

    /// <inheritdoc cref="Directors"/>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Sessions { get; set; }
    public string Version { get; set; } = "";

    /// <summary>
    /// The exact source commit this running image was built from (the short SHA stamped into the container
    /// as the COCKPIT_COMMIT build argument), or NULL when the responder has no such stamp - in which case
    /// it is OMITTED from the JSON. Unlike <see cref="Version"/>, which is a hand-bumped product version that
    /// does NOT change per commit, this identifies the precise deployed build. The deploy pipeline reads it
    /// to tell the OLD container apart from the NEW one during a redeploy: it polls /healthz until this
    /// reports the commit it just shipped, which is the only honest signal that the new image is actually
    /// serving traffic (the old container answers 200 right up until it is recycled). Fleet-global build
    /// identity, identical for every tenant, so it carries no per-tenant fact and is safe on hosted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get; set; }

    /// <summary>
    /// The identity of the running PROCESS answering this probe, unique per boot, or NULL when the
    /// responder does not publish one - in which case it is OMITTED from the JSON.
    ///
    /// The commit cannot answer "which process is this": a deploy runs two containers at once, and a
    /// redeploy of the same commit - a rollback, a retried release - puts two processes on the wire with
    /// identical commit stamps. A Gateway asks the public address for this field and compares it to its
    /// own to learn whether IT is the one serving production, which is what decides whether it does
    /// background work (issue #2398, GatewayInstanceRole). Fleet-global process identity carrying no
    /// per-tenant fact, so it is safe on the public probe exactly as the commit is.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Instance { get; set; }

    public DateTime ServerTime { get; set; } = DateTime.UtcNow;

    /// <summary>Director's GUID. Empty when returned by the Gateway aggregator.</summary>
    public string? DirectorId { get; set; }

    /// <summary>OS host name reporting the response.</summary>
    public string? MachineName { get; set; }
}
