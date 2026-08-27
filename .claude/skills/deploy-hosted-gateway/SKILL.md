---
name: deploy-hosted-gateway
description: The ONLY way to deploy the hosted Gateway, the Cockpit and the mobile app - all three ship in one image, so this one skill covers all three. Starts the GitHub deploy, watches it, and reports the measured outage. This is the CLOUD Gateway, not the downloadable desktop app. Triggers on "release the gateway", "deploy the gateway", "push the gateway live", "update the hosted gateway", "redeploy the gateway", "deploy the cockpit", "update the cockpit", "ship the cockpit", "deploy mobile", "deploy the mobile app", "deploy to production".
---

# Deploy the hosted Gateway

Updates the live cloud Gateway that DevThrottle clients talk to. It rebuilds the
Gateway container and pushes it live to Azure, then confirms the service is
healthy again.

**It covers the Cockpit and the mobile app too.** They are built INTO the Gateway
container image (`wwwroot/c` and `wwwroot/mobile` - see the repo-root `Dockerfile`).
There is no separate Cockpit deploy and no separate mobile deploy. If someone asks
you to "deploy the Cockpit", this is the skill.

This is NOT the desktop-app release. Cutting a version and building the
downloadable `cc-director.exe` is a different job - use the `release-manager`
skill for that. This skill only redeploys the cloud service; it never touches
version numbers, release notes, tags, or the mailing list.

## What you must know first

- **This workflow is the ONLY way to update the live Gateway.** Do not pin an
  image, restart the site, change app settings, or touch the staging slot by hand
  with `az`, and do not do it in the Azure Portal. Those paths skip the warmed
  hand-off, the outage measurement and the refusals below, and a hand-driven
  change is how you get an unmeasured outage on live sessions. If this workflow
  cannot do what is needed, that is a gap to fix in the workflow, not to work
  around. The emergency swap-back is also a workflow
  (`rollback-hosted-gateway.yml`) - use it rather than swapping by hand.
- **A person authorizes the go-live.** Starting this deploy pushes new code to
  the live service. Get an explicit go from the human before you start it. Do not
  start it on your own initiative.
- **It ships main, and never a commit whose checks have FAILED.** The run refuses, in
  seconds and before it touches anything, if it was started against any ref other than
  `refs/heads/main`, or if a check on the commit being shipped has COMPLETED and did
  not pass. Both refusals sit ahead of the Azure login and the build, so a refused
  deploy costs seconds and touches nothing.
- **A check that has not finished is NOT a refusal, and this deploy does not wait for
  one.** The run lists the pending checks and carries straight on, because a pending
  check is an absence of information rather than evidence of a fault. A commit with no
  checks at all is not refused either. Local verification is the gate; continuous
  integration is the backstop that reports afterwards. CLAUDE.md 5a says that about
  MERGES; the workflow reaches the same conclusion on its own account for deploys.

  This bullet said the opposite for twenty-five days, and the cost was real. The
  wait-for-CI wording was written on 2 August 2026 at 17:06 (#2389); the gate stopped
  behaving that way at 22:12 the SAME DAY (#2406, "stop the gate waiting for CI -
  refuse a FAILED check, never a pending one"). The step is even named "Refuse to
  deploy a commit whose checks have FAILED". Anyone who trusted this page instead of
  reading the workflow parked a deploy behind an hour of .NET CI that nothing asked
  for. If this page and
  `.github/workflows/deploy-hosted-gateway.yml` ever disagree again, the workflow is
  the truth and this page is the defect.
- **It does not set up any infrastructure.** The Azure resource group, container
  registry, database connection, and storage are already provisioned and persist
  across deploys. This deploy only: rebuild the image, point the live service at
  the new image, restart, verify health. It needs no stored secrets - it signs in
  to Azure through a trust that is already configured.

## Steps

### 1. Confirm the human's go

Do not proceed without an explicit "yes, deploy" from the human for this specific
release.

### 2. Start the release

One command starts it. It runs against `main` unless told otherwise:

```
gh workflow run deploy-hosted-gateway.yml --repo thefrederiksen/devthrottle --ref main
```

A human can do the same thing by hand: GitHub -> Actions -> "Deploy hosted
Gateway" -> "Run workflow". Both are the same trigger.

### 3. Watch it run to the end

Find the run that just started and watch it. The whole thing takes roughly five
minutes, because it builds the container in Azure.

```
gh run list --repo thefrederiksen/devthrottle --workflow deploy-hosted-gateway.yml -L 1
gh run watch <run-id> --repo thefrederiksen/devthrottle --interval 30 --exit-status
```

The run itself finishes with its own health check, so a green run already means it
came back up. If the run fails at "Azure login", the deploy trust is broken - see
Troubleshooting.

### 4. Confirm the live service is healthy

```
curl -s -m 20 -o /dev/null -w "HTTP %{http_code}\n" https://devthrottle-gw.azurewebsites.net/healthz
```

A `200` means the live Gateway is up.

**A blip here is NOT normal, and must not be waved through.** This used to say a
minute or two of `502`/`000` was expected cold-start behaviour and told you not to
report it. That was wrong, and it trained people to accept the exact failure that
took the live service down for 38.5 seconds on 2 August 2026 (issue #2383).

The deploy is a **warmed slot swap**: the old instance keeps serving until the new
one is proven healthy, and the swap itself has measured `Longest unavailable
stretch: 0.0s` across three consecutive deploys. There is no cold start on the
user path, because the user is never moved onto a cold instance.

**But the swap is not the whole cost of a deploy, and this section used to imply
it was.** It said a healthy deploy has "no user-visible gap at all". Around the
swap, a healthy deploy still costs roughly four seconds while the spare slot
starts, five while the old slot stops, and about ten in which Directors show
yellow and reconnect on their own - both slots share one worker, so starting and
stopping the spare disturbs production.

**Measured, so you know what normal looks like:** two consecutive healthy deploys
came in at **6.7s** (2026-08-19, run 32280188992) and **6.6s** (2026-08-20, run
32321987703) of longest external outage, with the swap window itself at 0.0s both
times. The second carried the change that moved the database open behind the port
bind, and it did not shift the number - which is how we know this cost is the slot
churn on a shared worker, not application startup. So around six or seven seconds
is what a good deploy costs here. The budget was five, which failed every deploy
and left the gate unable to tell a normal one from the two below; the owner set it
to ten on 2026-08-20 - "if the time gets over 10 seconds, then we start dealing
with it".

**And a deploy can be far worse than that, from a cause the swap number does not
cover.** On 12 August 2026 one took the live service off the air for **46.7
seconds** against what was then a five-second budget (issue #2585) - worse than the 38.5-second
failure above, and eight days after this section was written to prevent a repeat.
Neither outage was the cutover. In both, a second container failed its own
startup, the platform reverted by stopping the SITE, and stopping the site tore
down the healthy container that was serving traffic beside it.

So if `/healthz` does not answer `200` immediately after the run goes green,
something went wrong with the hand-off. Say so; do not wait it out.

**A failed run is a report, not a protection.** The watch job fails if the
external outage exceeds **ten seconds**, and it did fail on 12 August - users were
dark for 46.7 seconds regardless. A green run means production stayed inside the
budget; a red one tells you afterwards that it did not. Neither prevents an
outage, so never present a green run as proof that deploying is free.

v2.0.4 mitigates the 12 August cause: the Gateway now waits for its database
inside the platform's start budget instead of giving up at ninety seconds and
exiting. Its own code comments say it is a mitigation and NOT the fix. The fix is
to bind the port before the database work, so site startup never depends on
PostgreSQL (#2383's first recommendation), and that is unbuilt. Until it is,
expect that a deploy CAN take the service down for tens of seconds, and say so
when you report one.

### 5. Report plainly

Tell the human, in plain language, that the live Gateway was updated and is
healthy. Describe what changed, not run numbers.

## Troubleshooting

- **Run fails in about 20 seconds at "Azure login (OIDC)".** The Azure trust that
  lets GitHub deploy is missing or wrong. The deploy signs in as an Azure app
  registration that must carry a federated credential whose subject is
  `repo:thefrederiksen/devthrottle:environment:hosted-gateway-production`. Fixing
  this needs Azure access in the tenant that owns that app registration; bring the
  human in. Full context: the deploy workflow's own header comments in
  `.github/workflows/deploy-hosted-gateway.yml`, and the provisioning runbook at
  `docs/architecture/step3-azure-deploy` in the `devthrottle_internal` repository.
- **Health check never reaches 200 after several minutes.** The container is
  failing to start. Read the App Service logs in Azure (the human, or an agent
  authenticated to the Gateway subscription, can pull them). A failed database
  migration on startup is the usual cause.

## What this skill does not do

- It does not release the desktop app (`release-manager`).
- It does not provision or change Azure infrastructure or app settings.
- It does not auto-deploy on a commit. The live Gateway updates only when a person
  deliberately runs this. Committing to main never touches the live service.
