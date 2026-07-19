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
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;

    /// <summary>Director's GUID. Empty when returned by the Gateway aggregator.</summary>
    public string? DirectorId { get; set; }

    /// <summary>OS host name reporting the response.</summary>
    public string? MachineName { get; set; }
}
