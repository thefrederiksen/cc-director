namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (wave 3): the payload for the <c>recap-generate</c> tunnel verb
/// (POST /sessions/{sid}/recap). The only input the REST route carried was the optional <c>model</c>
/// query-string argument; it rides here so the tunnel verb and the REST route reach the SAME core with
/// identical inputs. A blank/absent model resolves to the recap default model inside the core, exactly
/// as the REST lambda resolved it.
/// </summary>
public sealed class RecapGenerateRequest
{
    /// <summary>The model to generate the recap with. Blank/absent falls back to the recap default.</summary>
    public string? Model { get; set; }
}
