namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /healthz response.
/// </summary>
public sealed class HealthDto
{
    public string Status { get; set; } = "ok";
    public int Directors { get; set; }
    public int Sessions { get; set; }
    public string Version { get; set; } = "";
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;

    /// <summary>Director's GUID. Empty when returned by the Gateway aggregator.</summary>
    public string? DirectorId { get; set; }

    /// <summary>OS host name reporting the response.</summary>
    public string? MachineName { get; set; }

    /// <summary>
    /// How many sessions are actively working (starting up or mid-turn) right now. The
    /// launcher's restart policy requires this to be zero before it may restart the Director
    /// to apply an update. Null when reported by a build that predates the field - which the
    /// policy treats as "unknown, do not restart".
    /// </summary>
    public int? BusySessions { get; set; }
}
