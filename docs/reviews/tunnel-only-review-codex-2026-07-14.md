# Tunnel Only Review

Review target: origin/main on 2026-07-14.

## 1. Tunnel Only Migration Review

### Finding 1.1: The main Gateway to Director command path is tunnel only.

Evidence: `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:12` to `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:16` says the old Director client is deleted and a null tunnel send means the Director is not connected. The router only constructs a `DirectorCommand` and calls the injected tunnel sender at `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:35` to `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:44`. The sender in `src/CcDirector.Gateway/GatewayHost.cs:1758` to `src/CcDirector.Gateway/GatewayHost.cs:1771` resolves the active SignalR connection and invokes the command over that connection.

Recommendation: Keep this architecture. Add a focused test or static check that fails if a new Gateway to Director Hypertext Transfer Protocol client is introduced.

### Finding 1.2: The Director still has a nontrivial local Hypertext Transfer Protocol floor, and one mode can still bind it to every network interface.

Evidence: the default branch binds loopback at `src/CcDirector.ControlApi/ControlApiHost.cs:276` to `src/CcDirector.ControlApi/ControlApiHost.cs:287`, but local area network mode binds `IPAddress.Any` at `src/CcDirector.ControlApi/ControlApiHost.cs:264` to `src/CcDirector.ControlApi/ControlApiHost.cs:272`. The code maps local routes for settings, agents, tools, workspaces, and scheduler at `src/CcDirector.ControlApi/ControlApiHost.cs:436` to `src/CcDirector.ControlApi/ControlApiHost.cs:443`. The comment says these are local floor routes, but also says remote configuration editing still needs tunnel verbs later at `src/CcDirector.ControlApi/ControlApiHost.cs:428` to `src/CcDirector.ControlApi/ControlApiHost.cs:435`.

Recommendation: Keep only if local area network mode is still an explicit supported deployment. If the migration goal is strict loopback only, remove the `IPAddress.Any` branch or guard it behind a development only setting. Also either complete the configuration tunnel verbs or clearly mark the current local floor as the final supported local only surface.

### Finding 1.3: The Gateway still exposes old Director registration and heartbeat routes.

Evidence: `POST /directors/register` is still mapped at `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:424` to `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:437`; `POST /directors/{id}/heartbeat` is still mapped at `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:439` to `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:470`; `DELETE /directors/{id}/registration` is still mapped at `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:510` to `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:516`. Current Director startup says it no longer registers or heartbeats over Hypertext Transfer Protocol at `src/CcDirector.ControlApi/GatewayClient.cs:517` to `src/CcDirector.ControlApi/GatewayClient.cs:524`. Tunnel registration happens in `src/CcDirector.Gateway/Streaming/DirectorHub.cs:77` to `src/CcDirector.Gateway/Streaming/DirectorHub.cs:83`.

Recommendation: Delete the registration and heartbeat routes after confirming there is no supported old Director compatibility requirement. Keep `POST /directors/{id}/doorbell` for now because current Director code still sends it at `src/CcDirector.ControlApi/GatewayClient.cs:803` to `src/CcDirector.ControlApi/GatewayClient.cs:815`.

### Finding 1.4: The migration is functionally ahead of its comments.

Evidence: `src/CcDirector.Gateway/GatewayHost.cs:1755` to `src/CcDirector.Gateway/GatewayHost.cs:1756` still says callers fall back to the old command path, while `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:13` to `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:16` says there is no fallback. Many route comments still describe a pre-cut fallback, for example `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1040` to `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1043`, `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1071` to `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1074`, and `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1090` to `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1095`.

Recommendation: Update these comments now. They are not harmless because future changes will copy the old fallback rule back into new code.

### Finding 1.5: The old phone client still uses Director addresses after creating or selecting a session.

Evidence: after a session is created through the Gateway, the phone client stamps the Director endpoint onto the session at `phone/CcDirectorClient/Voice/GatewayClient.cs:253` to `phone/CcDirectorClient/Voice/GatewayClient.cs:285`. The fleet parser falls back from `tailnetEndpoint` to `controlEndpoint` at `phone/CcDirectorClient/Voice/FleetParser.cs:40` to `phone/CcDirectorClient/Voice/FleetParser.cs:52`. Raw terminal HTML is built against a Director base address at `phone/CcDirectorClient/Voice/RawTerminalPage.cs:36` to `phone/CcDirectorClient/Voice/RawTerminalPage.cs:43`.

Recommendation: Either delete the old phone project if the React mobile application is the only supported phone experience, or migrate every phone operation to same origin Gateway routes. Keeping this client in the repository makes the migration look incomplete.

## 2. Leftover Director Representational State Transfer Cleanup

### Finding 2.1: Delete dead registration and heartbeat implementation in `GatewayClient`.

Evidence: `GatewayClient.Start` says the old register, heartbeat, and verify loop is gone at `src/CcDirector.ControlApi/GatewayClient.cs:517` to `src/CcDirector.ControlApi/GatewayClient.cs:524`. The timer field remains but is never assigned; references are only declarations and disposal in `src/CcDirector.ControlApi/GatewayClient.cs:62`, `src/CcDirector.ControlApi/GatewayClient.cs:544`, `src/CcDirector.ControlApi/GatewayClient.cs:567`, and the uncalled `HeartbeatTick` at `src/CcDirector.ControlApi/GatewayClient.cs:733`. `RegisterLoop`, `TryRegisterAsync`, `HeartbeatTick`, and related helpers remain at `src/CcDirector.ControlApi/GatewayClient.cs:634` to `src/CcDirector.ControlApi/GatewayClient.cs:818`.

Recommendation: Delete the old registration, heartbeat, and verification code from `GatewayClient`. Keep the active on-demand outbound calls such as doorbell and session number allocation.

### Finding 2.2: Delete or isolate old Gateway registration routes.

Evidence: the old `DirectorRegistry.Upsert` path still builds an entry with `Source = "http"` and a dialable control endpoint at `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs:79` to `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs:110`. The tunnel registration path builds an entry with empty control endpoint at `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs:120` to `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs:143`.

Recommendation: Delete `Upsert` and the Gateway route that calls it if old Directors are not supported. If old compatibility is required, move it behind a named legacy setting and keep it out of the normal tunnel-only path.

### Finding 2.3: Delete `DirectorForwarding` and the unused forwarding package.

Evidence: `src/CcDirector.Gateway/DirectorForwarding.cs:3` to `src/CcDirector.Gateway/DirectorForwarding.cs:20` describes direct proxying to Director control endpoints. The only code reference is a stale comment at `src/CcDirector.Gateway/GatewayHost.cs:997`. The Gateway project still references `Yarp.ReverseProxy` at `src/CcDirector.Gateway/CcDirector.Gateway.csproj:25` to `src/CcDirector.Gateway/CcDirector.Gateway.csproj:27`, but the only code hits for Yarp are this package reference and obsolete documentation.

Recommendation: Delete `DirectorForwarding.cs`, remove `builder.Services.AddHttpForwarder()` at `src/CcDirector.Gateway/GatewayHost.cs:997` to `src/CcDirector.Gateway/GatewayHost.cs:998`, and remove the package reference if no other branch needs the one address proxy.

### Finding 2.4: Delete or rewrite verification contracts.

Evidence: `DirectorVerification.cs` describes a Gateway callback to the Director at `src/CcDirector.Gateway.Contracts/DirectorVerification.cs:3` to `src/CcDirector.Gateway.Contracts/DirectorVerification.cs:8` and a WebSocket callback leg at `src/CcDirector.Gateway.Contracts/DirectorVerification.cs:50` to `src/CcDirector.Gateway.Contracts/DirectorVerification.cs:63`. The Gateway route comment says that callback handshake is deleted at `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:505` to `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:508`. Current `VerifyAsync` still posts to `/directors/{id}/verify` at `src/CcDirector.ControlApi/GatewayClient.cs:880` to `src/CcDirector.ControlApi/GatewayClient.cs:912`, but no Gateway route for that path remains.

Recommendation: Delete `VerifyAsync`, `DirectorVerifyRequest`, `DirectorVerifyResultDto`, and `VerifyCallbackDto`, or replace them with a tunnel health check that does not dial a Director.

### Finding 2.5: Keep the Director doorbell route, but fix the wording.

Evidence: current Director code still sends `POST /directors/{id}/doorbell` at `src/CcDirector.ControlApi/GatewayClient.cs:803` to `src/CcDirector.ControlApi/GatewayClient.cs:815`; the Gateway consumes it at `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:479` to `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:490`. The contract says the Gateway pulls truth over Director representational state transfer after the ping at `src/CcDirector.Gateway.Contracts/DoorbellRequest.cs:3` to `src/CcDirector.Gateway.Contracts/DoorbellRequest.cs:10`, which no longer matches the tunnel push model.

Recommendation: Keep the route as an outbound Director to Gateway notification until it is replaced by a tunnel upstream event. Update the contract comment to say the periodic tunnel snapshot reconciles missed pings.

## 3. Connection Resilience On Bad Internet

### Finding 3.1: Tunnel reconnect is reasonable, but the fixed long-outage retry is too aggressive for weak mobile networks.

Evidence: the Director tunnel client uses automatic reconnect delays of zero, two, five, and ten seconds at `src/CcDirector.ControlApi/GatewayStreamClient.cs:156` to `src/CcDirector.ControlApi/GatewayStreamClient.cs:159`. After that gives up, the supervise loop redials every five seconds at `src/CcDirector.ControlApi/GatewayStreamClient.cs:228` to `src/CcDirector.ControlApi/GatewayStreamClient.cs:239`.

Recommendation: Keep the fast first ladder, then change the long-outage loop to randomized increasing delay with a maximum around thirty to sixty seconds. Reset it after a successful reseed. This reduces repeated failed connection attempts on a weak phone hotspot without making normal brief drops slower.

### Finding 3.2: State recovery after reconnect is good for roster state.

Evidence: on reconnect the client sends `Hello` and a full snapshot at `src/CcDirector.ControlApi/GatewayStreamClient.cs:275` to `src/CcDirector.ControlApi/GatewayStreamClient.cs:292`. The pushed store treats each connection as a new generation at `src/CcDirector.Gateway/Streaming/PushedSessionStore.cs:47` to `src/CcDirector.Gateway/Streaming/PushedSessionStore.cs:68`, drops stale sequence messages at `src/CcDirector.Gateway/Streaming/PushedSessionStore.cs:12` to `src/CcDirector.Gateway/Streaming/PushedSessionStore.cs:24`, and keeps quiet sessions fresh with periodic full repush at `src/CcDirector.ControlApi/GatewayStreamClient.cs:111` to `src/CcDirector.ControlApi/GatewayStreamClient.cs:136`.

Recommendation: Keep this. It is the strongest part of the migration.

### Finding 3.3: Live streams are torn down correctly, but user work in in-flight commands is not replayed.

Evidence: the tunnel client cancels all upstream producers during reconnect and close at `src/CcDirector.ControlApi/GatewayStreamClient.cs:167` to `src/CcDirector.ControlApi/GatewayStreamClient.cs:178`. The browser terminal reopens the stream and replays terminal history on the next connection at `packages/client-core/src/terminal/interactive.ts:376` to `packages/client-core/src/terminal/interactive.ts:403`. However, command writes such as prompt, escape, and interrupt are one-shot requests at `packages/client-core/src/api/client.ts:423` to `packages/client-core/src/api/client.ts:465`. They use `gatewayFetch` without a timeout or durable retry; the fetch helper says one-shot writes pass no timeout at `packages/client-core/src/api/client.ts:264` to `packages/client-core/src/api/client.ts:270`.

Recommendation: Add a command request shape that is safe to repeat for user-submitted prompts and destructive controls, or at least add a visible pending and retry state for writes that fail while the tunnel is down. Dictation already uses stronger durability; normal typed commands should not be weaker.

### Finding 3.4: The mobile read-only terminal reconnects every one point two seconds forever.

Evidence: the read-only terminal has a fixed reconnect delay of one point two seconds at `packages/client-core/src/terminal/stream.ts:39`, and its close handler always schedules another connection with that same delay at `packages/client-core/src/terminal/stream.ts:420` to `packages/client-core/src/terminal/stream.ts:434`. The interactive terminal is better: it uses thirty fast attempts and then a fifteen second slow probe at `packages/client-core/src/terminal/interactive.ts:56` to `packages/client-core/src/terminal/interactive.ts:61` and `packages/client-core/src/terminal/interactive.ts:461` to `packages/client-core/src/terminal/interactive.ts:481`.

Recommendation: Give the mobile terminal the same slow keepalive behavior as the interactive terminal. On weak mobile internet, a permanent one point two second loop wastes battery and radio time.

### Finding 3.5: Dictation upload is the best current model for weak connections.

Evidence: dictation upload registers with an idempotency key at `packages/client-core/src/api/client.ts:1495` to `packages/client-core/src/api/client.ts:1508`, stores the audio locally before chunk planning at `packages/client-core/src/api/client.ts:1535` to `packages/client-core/src/api/client.ts:1538`, resumes missing chunks at `packages/client-core/src/api/client.ts:1560` to `packages/client-core/src/api/client.ts:1599`, and treats a swept server staging area as retryable at `packages/client-core/src/api/client.ts:1601` to `packages/client-core/src/api/client.ts:1605`.

Recommendation: Use this pattern for other user work that must survive a tunnel or phone network drop.

## 4. Outdated Documentation

### Finding 4.1: `docs/architecture/cockpit/COCKPIT_DESIGN.md` should be updated or archived.

Evidence: it says to lift a typed client from `DirectorEndpointClient` at `docs/architecture/cockpit/COCKPIT_DESIGN.md:81` to `docs/architecture/cockpit/COCKPIT_DESIGN.md:85`. It says the terminal opens a direct WebSocket to the owning Director at `docs/architecture/cockpit/COCKPIT_DESIGN.md:105` to `docs/architecture/cockpit/COCKPIT_DESIGN.md:108` and again at `docs/architecture/cockpit/COCKPIT_DESIGN.md:117` to `docs/architecture/cockpit/COCKPIT_DESIGN.md:122`.

Recommendation: Update if this is still the Cockpit architecture page. Otherwise move it to an archive folder and put a short replacement that says the browser talks to the Gateway and the Gateway reaches Directors through the tunnel.

### Finding 4.2: `docs/architecture/gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md` should be updated.

Evidence: it lists `DirectorEndpointClient` as the per-Director client at `docs/architecture/gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md:77` to `docs/architecture/gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md:83`, and says there is no cache of session data and every `/sessions` call fans out live at `docs/architecture/gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md:89`.

Recommendation: Update. This is a general architecture document, so stale content here will mislead new work.

### Finding 4.3: `docs/architecture/gateway/DIRECTOR_LIVENESS_PLAN.md` should be deleted or marked historical.

Evidence: it is built around heartbeat expiry and probing Director control endpoints at `docs/architecture/gateway/DIRECTOR_LIVENESS_PLAN.md:11` to `docs/architecture/gateway/DIRECTOR_LIVENESS_PLAN.md:19`, and still points to `DirectorEndpointClient` at `docs/architecture/gateway/DIRECTOR_LIVENESS_PLAN.md:58` to `docs/architecture/gateway/DIRECTOR_LIVENESS_PLAN.md:63`.

Recommendation: Delete if no one needs the history. Otherwise move it under an archive folder and mark it as pre-tunnel.

### Finding 4.4: `docs/new_architecture/phase-1-full-bidirectional-spec.md` should be archived.

Evidence: it tells implementers to keep the old fallback path at `docs/new_architecture/phase-1-full-bidirectional-spec.md:86` to `docs/new_architecture/phase-1-full-bidirectional-spec.md:93`, which is no longer true.

Recommendation: Archive as a completed phase document. Do not leave it in an active architecture folder.

### Finding 4.5: `docs/new_architecture/OVERNIGHT-STATUS.md` should be archived.

Evidence: it describes the old flag-gated and fallback world repeatedly, including preserved fallback at `docs/new_architecture/OVERNIGHT-STATUS.md:15` to `docs/new_architecture/OVERNIGHT-STATUS.md:17` and a routing helper that falls back to `DirectorEndpointClient` at `docs/new_architecture/OVERNIGHT-STATUS.md:73` to `docs/new_architecture/OVERNIGHT-STATUS.md:77`.

Recommendation: Archive. It is useful history, but it should not be an active status source after the cut.

## 5. Repository Cleanup

### Finding 5.1: Remove checked-in backup files.

Evidence: `phone/CcDirectorClient/TalkPage.xaml.cs.fixbak` and `phone/CcDirectorClient/TalkPage.xaml.fixbak` are tracked. They are duplicate backup files, and the main phone code also contains stale direct Director paths.

Recommendation: Delete both backup files.

### Finding 5.2: Clean up proof output that is not durable documentation.

Evidence: tracked proof output includes logs, test output, generated spreadsheets, and audio files such as `docs/cencon/proof/issue-509/ask-sequence.log`, `docs/cencon/proof/issue-887/live-transcription/tts-clip.wav`, `docs/theme-gallery/test-repro-output.xlsx`, and multiple `test-output.txt` and `summary.json` files under `docs/cencon/proof`.

Recommendation: Move large proof artifacts to external storage or an archive package, and keep only short written proof summaries in the repository.

### Finding 5.3: Remove unused one address forwarding dependency if the proxy is gone.

Evidence: the project still references `Yarp.ReverseProxy` at `src/CcDirector.Gateway/CcDirector.Gateway.csproj:25` to `src/CcDirector.Gateway/CcDirector.Gateway.csproj:27`, and still calls `AddHttpForwarder` at `src/CcDirector.Gateway/GatewayHost.cs:997` to `src/CcDirector.Gateway/GatewayHost.cs:998`. No live code uses the forwarder after the tunnel cut.

Recommendation: Delete the dependency and service registration together with `DirectorForwarding.cs`.

### Finding 5.4: Decide whether the old `phone` tree is still shipped.

Evidence: the solution file does not list `CcDirectorClient`, while the phone tree still has direct Director endpoint code and backup files. The current web mobile application uses same-origin Gateway calls through `packages/client-core/src/api/client.ts:380` and nearby routes, while the phone tree keeps its own transport model.

Recommendation: If the web mobile application is the supported phone surface, archive or delete the old `phone/CcDirectorClient` tree. If it is still shipped, migrating it to Gateway-only routes should be treated as part of finishing the tunnel-only cut.
