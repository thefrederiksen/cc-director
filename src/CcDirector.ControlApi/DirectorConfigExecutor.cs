using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup CUT RESTORATION: the Director-level CONFIG area of the tunnel command surface - the
/// settings read and write behind the Cockpit's Director Settings editor.
///
/// The cut dropped the Gateway's HTTP reverse-proxy leg for <c>/directors/{id}/settings</c> and deferred
/// remote config editing to Phase 4, but the Cockpit's caller was never removed with it. With no route
/// mapped, the Gateway's single-page-app fallback answered the Cockpit's GET with the HTML shell at status
/// 200 - so the editor silently loaded a web page where the settings were meant to be, and the client could
/// not even tell it had failed (it only checks <c>res.ok</c>). These two verbs give the surface the proper
/// tunnel legs the cut always intended it to have, instead of leaving the caller pointing at nothing.
///
/// Each core reproduces the lambda of the Director's old SettingsEndpoint route verbatim (that route died with the Director's listener in the Remove-the-network-port mission; these tunnel verbs are the one remaining remote surface) - the SAME
/// <see cref="CcDirectorConfigService"/> calls, guards, and return shapes - so the loopback floor route and
/// this tunnel verb share one behaviour and cannot drift. The config surface itself stays on the loopback
/// floor for LOCAL callers exactly as before; this only restores the REMOTE read/write path.
/// </summary>
internal sealed class DirectorConfigExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        "settings-get",
        "settings-put",
    };

    public async Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return command.Verb switch
        {
            "settings-get" => SettingsGet(),
            "settings-put" => await SettingsPut(context.Services?.ReapplyGatewayAsync, command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the director config area"),
        };
    }

    /// <summary>
    /// The <c>settings-get</c> verb (director-level, no session): the full config.json as a JSON object.
    /// Mirrors the Director's <c>GET /settings</c> lambda - a read of
    /// <see cref="CcDirectorConfigService.ReadRaw"/>, always a 200. The body is the config object itself
    /// (an opaque object the Director owns), which the Gateway forwards to the Cockpit verbatim.
    /// </summary>
    internal static DirectorCommandResult SettingsGet()
    {
        FileLog.Write("[DirectorConfigExecutor] settings-get");
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(CcDirectorConfigService.ReadRaw()));
    }

    /// <summary>
    /// The <c>settings-put</c> verb (director-level, no session): deep-merge a partial patch into config.json
    /// and return the merged result. Mirrors the Director's <c>PUT /settings</c> lambda - a non-object body is
    /// a BadRequest with the same wording, the merge is the same
    /// <see cref="CcDirectorConfigService.MergePatch"/> call, and a patch touching the <c>gateway</c> block
    /// re-applies the Gateway live exactly as the route does (everything else is read on next use).
    ///
    /// The re-apply hook is the one dependency the tunnel command surface did not carry; it rides in
    /// <see cref="SessionCommandServices.ReapplyGatewayAsync"/>. Unlike the optional side-effect services, a
    /// MISSING hook is not skipped quietly: the REST route always has one, so its absence here means the host
    /// wired the stream client wrong, and silently merging gateway settings that never take effect would be
    /// precisely the "looks like it worked" failure this restoration exists to remove. It fails loudly instead,
    /// BEFORE writing anything, so the config on disk never disagrees with the running Director.
    /// </summary>
    internal static async Task<DirectorCommandResult> SettingsPut(Func<Task>? reapplyGatewayAsync, DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<SettingsPutRequest>(command.PayloadJson);
        if (request?.Settings is not JsonObject patch)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "request body must be a JSON object");

        FileLog.Write($"[DirectorConfigExecutor] settings-put: keys={string.Join(",", patch.Select(kv => kv.Key))}");

        var touchesGateway = patch.ContainsKey("gateway");
        if (touchesGateway && reapplyGatewayAsync is null)
        {
            FileLog.Write("[DirectorConfigExecutor] settings-put REFUSED: gateway patch with no re-apply hook wired");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error,
                "This Director cannot apply a gateway settings change over the tunnel: its re-apply hook is not wired. Nothing was written.");
        }

        var merged = CcDirectorConfigService.MergePatch(patch);

        if (touchesGateway)
            await reapplyGatewayAsync!();

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(merged));
    }
}

/// <summary>
/// The <c>settings-put</c> payload: the raw settings patch the Cockpit typed, carried verbatim as an opaque
/// JSON object. It is wrapped in a property rather than sent bare so the payload stays a well-formed command
/// envelope the executor can deserialize like every other verb.
/// </summary>
internal sealed class SettingsPutRequest
{
    public JsonNode? Settings { get; set; }
}
