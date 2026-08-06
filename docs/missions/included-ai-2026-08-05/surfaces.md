# Surface inventory - every place a non-admin member can see cost, credits, balance, or a top-up control

Mission: Included AI (issue #1360), phase 2 work item. Swept 5 August 2026 by the
phase-2 Manager against the product repo at commit c9564222f (worktree cut from
origin/main) and the website source in the mission worktree
`devthrottle_internal-wt-included-ai/website`.

Method: swept by KIND of surface, not by memory - the known trap here is one
mechanism feeding many surfaces. For each surface: what it shows, the file paths,
and whether administrators keep it. "Admin" means the member's `role` array on the
cloud `members` table includes `admin` (the same ruling the phase-1 inclusion
module uses).

The single most important finding is at the top: almost every product-repo cost
surface is fed by ONE of two mechanisms - the shared 402 "needs credit" state
machine, and the account credit-balance proxy. Hide the mechanism and every
surface downstream of it goes quiet; hide surfaces one by one and one will be
missed.

---

## Kind 1 - the shared "hosted AI needs credit" state machine (ONE mechanism, MANY surfaces)

The single source of the out-of-credits user experience. Every voice, wingman,
dictation, and text-to-speech surface in the whole product renders what this
machinery decides.

The mechanism (product repo):

- `src/CcDirector.Core/HostedAi/HostedAiState.cs` - the states. `NeedsCredits`
  and `CapReached` are the two money states.
- `src/CcDirector.Core/HostedAi/HostedAiMessages.cs` - the ONE copy source.
  `NeedsCredits` = "Voice needs credit. Add $5 to turn on transcription, voice
  mode, and Wingman - enough to last most of a month." with call-to-action
  "Add credits" opening the Billing page. `CapReached` = "You've hit your monthly
  spending limit. Raise it in Billing to keep going." with "Open Billing".
- `HostedAiUrls.Billing` (same directory) - the top-up deep link every
  call-to-action opens.
- `src/CcDirector.Core/HostedAi/HostedAiErrorMapper.cs` - maps a 402 body's
  `code` to a state: `insufficient_credits` -> NeedsCredits,
  `monthly_limit_reached` -> CapReached, and EVERY OTHER OR UNKNOWN CODE ->
  NeedsCredits. This default is a trap for this mission: the phase-1 proxy now
  answers `subscription_required` and `fair_use_limit_reached`, and today's
  mapper would show BOTH as "Voice needs credit. Add $5" - a credits message,
  with a top-up button, to exactly the people the owner ruled must never see one.
- `src/CcDirector.Core/HostedAi/HostedAiReadiness.cs` - the PRE-FLIGHT gate:
  balance at or below zero -> NeedsCredits, which blocks recording surfaces
  before any server call. After phase 1 an entitled member with a zero balance
  is served by the cloud, so this client-side balance gate now blocks people the
  server would serve - it fails the mission's own acceptance test (a fresh
  zero-balance trial account completes a dictation round-trip).
- `src/CcDirector.Core/HostedAi/DirectorHostedAiReadiness.cs` - the desktop
  variant of the same gate (reads the balance through the Gateway).
- `src/CcDirector.Core/HostedAi/HostedAiPayload.cs`, `HostedAiMessage.cs`,
  `HostedAiMessages.cs` - the shared 402 wire body `{ error, state, text,
  ctaLabel, ctaAction, ctaUrl }`.
- `src/CcDirector.Gateway/HostedAi/HostedAiHttp.cs` - stamps the state onto
  `SessionDto.VoiceUnavailable` and serves the 402 body.
- `src/CcDirector.Gateway/Wingman/VoiceDisplayFold.cs` - folds the state into
  the voice screen verdict: badge "Voice needs credit", message, and which
  actions are offered.
- `src/CcDirector.Gateway/CarMode/CarModeChat.cs` - a money 402 inside Car Mode
  raises the same shared message (spoken and shown).

The renderers (every one shows the mechanism's output verbatim):

| Surface | Files | What a non-admin sees today |
|---|---|---|
| Mobile app root banner | `apps/mobile/src/components/CreditsNotice.tsx`, mounted in `apps/mobile/src/main.tsx`; emitter in `packages/client-core/src/api/client.ts` (`CreditsError`, `creditsErrorFrom`, `onCreditsNeeded`, plus fallback copy "Voice needs credit. Add credits to turn it on." and default label "Add credits") | Banner with the credits message and an "Add credits" button deep-linking to Billing |
| Cockpit inline errors | `packages/client-core/src/api/client.ts` (`gatewayErrorMessage` returns the CreditsError message); no app-root banner in the cockpit | The credits sentence inline wherever the error is shown |
| Voice screen (mobile and cockpit) | `packages/client-core/src/voice/voiceRowState.ts`, `useVoiceMode.ts`; fed by `SessionDto.VoiceUnavailable` | "Voice needs credit" badge, message, call-to-action |
| Director desktop dialogs | `src/CcDirector.Avalonia/HostedAiUnavailableDialog.axaml(.cs)`, `HostedAi/DesktopHostedAiGate.cs`, `HostedAi/DesktopHostedAiCta.cs` (opens the Billing page), `Voice/SpeakDialog.axaml.cs`, `Voice/DesktopTtsPlayer.cs` | Modal "add credits" dialog before and during dictation, wingman, and read-aloud |
| Android native app | `phone/CcDirectorClient/Voice/HostedAiUnavailable.cs` (parser plus its OWN hardcoded defaults: "Voice needs credit. Add credits to turn it on.", "Add credits"), `Voice/DirectorVoiceClient.cs`, `TalkPage.xaml.cs` | The same message with an "Add credits" prompt opening Billing |
| Car Mode (spoken) | `src/CcDirector.Gateway/CarMode/CarModeChat.cs` | The credits message spoken out loud |

Admins keep it? The mechanism must stop producing CREDIT messages for included
services for EVERYONE - after this mission a 402 on an included service is never
"add credits" (it is "subscribe" or "fair-use limit reached", both no-numbers).
The direct-API credits path still 402s with today's wording (design C4), but that
error goes to API callers, not through this state machine's voice surfaces.

## Kind 2 - the account credit-balance proxy and its readers

The mechanism:

- `src/CcDirector.Gateway/Api/AccountCreditsEndpoint.cs` - `GET /account/credits`
  on the Gateway: proxies the cloud balance with the Gateway's stored account
  token. Serves `balanceMicros` and `lastDebitMicros`.
- `src/CcDirector.Gateway.Contracts/AccountCreditsDto.cs` - the wire body.
- `src/CcDirector.Core/Account/AccountCreditsClient.cs` - the cloud read
  (`GET /api/v1/account/credits`, JWT-authed).
- `src/CcDirector.Core/Account/GatewayAccountCreditsClient.cs` - the Director's
  read of the Gateway proxy.

The readers:

| Surface | Files | What a non-admin sees today |
|---|---|---|
| Car Mode voice tools `get_credits` and `get_spend` | `src/CcDirector.Gateway/CarMode/CarModeBrain.cs` (tool definitions and prompt text about credits), `LoopbackCarModeFleet.cs`, `CarModeModels.cs` (`CarModeCredits`); spend side: `src/CcDirector.Gateway/Governance/AccountHostedAiSpendStore.cs`, `HostedAiSpendSweep.cs`, `Data/Entities/AccountHostedAiSpendEntity.cs` | Asks "how are we doing on credits" and the assistant SPEAKS the dollar balance, the last action's cost, and trailing hosted-AI spend |
| Fleet Assistant (cockpit and mobile) | same brain tooling via `src/CcDirector.Gateway/Api/FleetBrainEndpoint.cs`; suggestion copy in `apps/cockpit/src/assistant/AssistantView.tsx` ("How are we doing on credits?") and `apps/mobile/src/pages/Assistant.tsx` (same line) | Typed answer with dollar amounts; the UI itself suggests asking about credits |
| Desktop pre-flight balance read | `src/CcDirector.Avalonia/HostedAi/DesktopHostedAiGate.cs` (2-second credit read before dictation), `MainWindow.axaml.cs` (comment at the Speak entry) | Not a display by itself, but it is what turns a zero balance into the blocking "add credits" dialog (Kind 1) |
| Cockpit / mobile settings | NONE found: `apps/cockpit/src/account/AccountView.tsx` shows no balance; `/account/credits` appears in `packages/client-core/src/api/schema.ts` only, with no caller | Nothing today - the React rebuild dropped the balance display; the endpoint remains live for any client |

Admins keep it? Recommendation (question Q1 below): the product has NO signal
for the member's admin role today, so these cannot be admin-gated in the product
repo without new design. Since credits now exist only for direct API callers,
the recommendation is that in-product balance reads and voice tools go away for
everyone, and money questions live on the website (Billing for API users, the
admin pages for administrators).

## Kind 3 - cost and credit WORDING on settings, health, and onboarding surfaces

| Surface | Files | What it says | Keep? |
|---|---|---|---|
| AI settings tab (cockpit and phone, shared) | `packages/client-core/src/settings/AiTab.tsx` line 206 | "Hosted models on your DevThrottle account. Billed to your account credits." | HIDE/REWORD for everyone - the sentence is now FALSE (included AI is part of the subscription, not billed to credits) |
| Transcription health report reasons | `apps/cockpit/src/transcription/TranscriptionHealthView.tsx` ("out_of_credits" -> "ran out of credits"); classification from the Gateway (`src/CcDirector.Gateway/Supervision/TerminatingFaultClassifier.cs`, `SessionFault.cs`, transcription pipeline files) | Health rows can name "ran out of credits" as a failure reason | Reason stays truthful for the direct-API path; after inclusion an included call can no longer fail this way. New refusal codes need their own truthful reasons (see Q3) |
| Assistant example prompts | `apps/cockpit/src/assistant/AssistantView.tsx` (subtitle "...credits, schedules" and example "How are we doing on credits?"), `apps/mobile/src/pages/Assistant.tsx` | The UI invites a credits question | Follows the Q1 ruling: if the credits tool goes, the example copy goes with it |
| First-run wizard | `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml` (lines about the trial) | "No credit card." | KEEP - reassurance that there is NO cost, not a cost display |

## Kind 4 - the wingman and cleanup model identities (the alias switch and the picker, design C3)

Not cost displays, but the money-bearing model routing this phase changes -
listed so the hiding and the switch land as one review.

- `src/CcDirector.Core/Configuration/TranscriptionEndpoint.cs` -
  `TranscriptionEndpointResolver` constants: `DevThrottleWingmanModel`
  ("zai-org/GLM-5.2" -> `devthrottle/wingman`), `DevThrottleWingmanFastModel`
  ("Qwen/Qwen2.5-72B-Instruct" -> `devthrottle/wingman-fast`),
  `DevThrottleDictationCleanupModel` ("o4-mini" -> `devthrottle/dictation-cleanup`).
- `src/CcDirector.Core/Configuration/WingmanModelConfig.cs` - resolution honors
  ANY saved `brain_model` / `brain_model_fast` string verbatim (except the old
  Claude aliases). A saved CATALOG id would keep billing credits after the
  switch, which ruling 1 forbids - resolution must fall forward to the
  devthrottle ids for anything that is not one of them.
- `src/CcDirector.Gateway/Settings/TenantSettingsResolver.cs` - the hosted
  per-tenant wingman model overrides (same must-not-honor-catalog-ids rule).
- `src/CcDirector.Core/Dictation/CleanupOrchestrator.cs` (`DefaultModel`) and
  `src/CcDirector.Core/Configuration/AgentOptions.cs` (`DictationCleanupModel`)
  - the dictation-cleanup call sites; session naming rides the fast wingman
  resolution.
- The picker: `src/CcDirector.Gateway/Api/AiModelsEndpoint.cs`
  (`GET /gateway/ai/models?kind=chat` relays the DevThrottle catalog) feeding
  the "Thinking model" and "Fast model" dropdowns in
  `packages/client-core/src/settings/AiTab.tsx`. Per design C3 the wingman
  pickers must stop offering catalog models; the Gateway (which owns every
  ruling) must serve only the wingman ids for the chat kind. The speech-model
  picker is unaffected (text-to-speech is included in its entirety).

## Kind 5 - emails

- Product repo: NONE. The Gateway sends no member-facing email today (the
  transcription suggestion email block carries no money content; the
  DevThrottle-to-self email channel is a planned capability, not shipped).
- Website repo: no member-facing credit email found. `api/_lib/email.js` and
  `api/_lib/notify.js` carry no credit or balance content. `api/daily-report.js`
  and `api/morning-report.js` DO carry top-up reconciliation and the outstanding
  credit-liability figure, but they are the OWNER's operational reports -
  admin-kept by construction. Stripe's own checkout receipts are Stripe's and
  out of scope (pricing and Stripe untouched by this mission).

## Kind 6 - website repo surfaces (inventory only - the hiding waits for the phase-1 merge and is NOT phase 2's product-repo work)

Member-facing, non-admin members must stop seeing these:

| Surface | Files | What it shows |
|---|---|---|
| Account page credits chip | `website/src/pages/Account.jsx` (reads `/account/credits?limit=1`, renders a "Credits $x.xx" chip linking to Billing when the balance is positive) | Balance in dollars plus a Billing link |
| Billing page credit machinery | `website/src/pages/Billing.jsx` | The balance, top-up presets ($5 minimum), automatic top-up card with threshold and amount, monthly spending cap, card-on-file, the credit ledger ("Credit added by DevThrottle", signed amounts), low-balance notes |
| Usage page | `website/src/pages/Usage.jsx` | "Rate-card charges, all services - paid from your credits" chart and totals, plus the "Credit balance & top-up ->" link. The COGS reveal on this page is ALREADY admin-only - that part is kept |
| Auth context / dashboard chrome | `website/src/contexts/AuthContext.jsx` and any shared chrome that carries the credits chip | Wherever the chip mechanism is shared |

Public, signed-out marketing and documentation pages that NAME credits or prices
(`website/src/pages/Models.jsx` rate card, `Pricing.jsx`, `Landing.jsx`,
`Signup.jsx`, `Transcription.jsx`, `Terms.jsx`, `Privacy.jsx`, the docs under
`website/src/content/docs/` - account/billing, account/usage, the api pages -
and `docsSearchIndex.json` which is generated from them): these document the
credits product that STILL EXISTS for direct API callers, and they are visible
to the signed-out public, not "a normal user inside the product". Recommendation:
KEEP, flagged for the Architect's website phase to confirm scope.

Admin surfaces that keep everything: `website/src/pages/AdminUsage.jsx`,
`website/src/pages/Admin.jsx`, `website/src/components/admin/MemberRow.jsx`,
`MembersGrid.jsx`, `MemberDrawer.jsx`, `website/api/v1/admin.js`,
`website/api/_lib/admin-usage.js`, `api/report.js`, `api/daily-report.js`,
`api/morning-report.js`.

## Considered and excluded, with reasons

- Claude token usage displays (`src/CcDirector.Core/Claude/ClaudeUsageService.cs`
  and its consumers): the member's OWN Anthropic plan usage, not DevThrottle
  cost. Not a credits surface.
- Session and workflow token counters in the cockpit: agent token counts, not
  dollars. Not a credits surface.
- `InsufficientCreditsException` (`src/CcDirector.Core/Transcription/`): plumbing
  that FEEDS Kind 1, not a surface; its user-visible output is inventoried there.
- The catalog 402 wording for direct API callers ("top up" message): kept
  by design C4, byte-for-byte, owner-flagged in the final report.

## Questions for the Architect (asked 5 August; ANSWERED same day - the rulings are recorded verbatim below each question)

- Q1 (admin signal): the product repo has NO way to know the member's admin role
  today - no endpoint the Gateway or clients read carries `role`. So the
  in-product credit surfaces (Kind 2: the `/account/credits` proxy, the Car Mode
  and Assistant credit and spend voice tools) cannot be admin-gated without new
  design. RECOMMENDATION: remove or disable them in-product for everyone -
  credits are now a direct-API-caller concern managed on the website - and let
  admins use the website's admin pages. Alternative: the cloud
  `/api/v1/account/credits` learns to answer "hidden" for non-admins after the
  phase-1 merge, and the Gateway proxies that verdict verbatim.

  RULING (Architect, 5 August 2026, verbatim): "Q1 APPROVED-REMOVE - kill every
  in-product credit reader and the Car Mode/Assistant credits+spend voice tools
  and their suggestion copy for EVERYONE (credits are a website concern; admins
  use the website admin pages), but KEEP the Gateway /account/credits wire
  endpoint alive for old-client compatibility - surfaces die, the wire stays."
- Q2 (pre-flight balance gate): `HostedAiReadiness` blocks recording when the
  balance is at or below zero. After inclusion this blocks entitled zero-balance
  members the server would serve, failing the acceptance test's dictation
  round-trip. RECOMMENDATION: retire the balance consultation (pre-flight always
  Ready in DevThrottle mode; the runtime 402 stays the authoritative gate, as
  the class's own contract already states).

  RULING (Architect, 5 August 2026, verbatim): "Q2 APPROVED - retire the
  client-side balance pre-flight entirely (always Ready in DevThrottle mode);
  the runtime 402 is the only gate."
- Q3 (new 402 codes): `HostedAiErrorMapper` maps unknown codes to NeedsCredits,
  so `subscription_required` and `fair_use_limit_reached` would render as "Add
  $5" with a top-up button. RECOMMENDATION: two new states with no-numbers,
  no-cost copy (subscription wording per design C5 pointing at
  devthrottle.com/pricing; fair-use wording per the design's plain message), and
  the Android parser's hardcoded credit defaults updated the same way.

  RULING (Architect, 5 August 2026, verbatim): "Q3 APPROVED-PLUS - two new
  states subscription_required (wording per design C5, points at
  devthrottle.com/pricing, no checkout promise) and fair_use_limit_reached
  (plain, resets next month), no numbers no cost in either; update the Android
  parser's hardcoded defaults identically; AND change the mapper's unknown-code
  default from NeedsCredits to a neutral no-money 'hosted AI unavailable' state
  - an unknown code must never claim credits."

Further rulings from the same reply, recorded verbatim:

- "Kind-4 approach endorsed (fall-forward to devthrottle ids for any
  non-devthrottle saved model; Gateway serves only wingman ids for chat kind;
  speech picker untouched)."
- "Kind-6 KEEP confirmed for public signed-out marketing/docs pages - ruling 2
  governs the signed-in product experience; flagged for the owner's report."

Manager note on scope, applied under the Kind-4 endorsement: the Car Mode model
setting (`CarModeModelConfig`, per-tenant `TenantSettingsResolver.CarModeModel`)
defaults to the fast wingman constant and its saved value is honored verbatim,
so the same fall-forward guard is applied there - a Car Mode pointed at a
catalog id would bill credits on an internal feature, which ruling 1 forbids.
Also discovered during the build: dictation cleanup is DETERMINISTIC in-process
code today (`CleanupOrchestrator` - no language model call at all; the model
string is a log label only), so the `devthrottle/dictation-cleanup` id switch
affects the constant, the log label, and the `AgentOptions` default - there is
no live cleanup traffic to re-route.
